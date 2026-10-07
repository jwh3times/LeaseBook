using System.Text;
using System.Text.Json;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.Web.Payments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace LeaseBook.Tests.Integration;

public sealed class SimulationSettingsTests
{
    [Theory]
    [InlineData("Production", "Simulation")]
    [InlineData("Staging", "Simulation")]
    [InlineData("Development", "Live")]
    [InlineData("Development", "Stripe")]
    public void Unsupported_environment_or_provider_mode_fails_at_startup(string environment, string mode)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Payments:Mode"] = mode }).Build();
        Should.Throw<InvalidOperationException>(() => SimulationSettings.Read(config, new EnvironmentStub(environment)));
    }

    [Theory]
    [InlineData("Payments:ApiKey")]
    [InlineData("Stripe:SecretKey")]
    [InlineData("Stripe")]
    [InlineData("Payments:Stripe:SecretKey")]
    [InlineData("Payments:Stripe")]
    public void Provider_credentials_are_refused_even_when_payments_are_disabled(string name)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [name] = "not-a-real-key" }).Build();
        Should.Throw<InvalidOperationException>(() => SimulationSettings.Read(config, new EnvironmentStub("Development")));
    }

    [Fact]
    public void Default_mode_cannot_route_a_customer_to_simulation()
    {
        var settings = SimulationSettings.Read(new ConfigurationBuilder().Build(), new EnvironmentStub("Production"));
        settings.Enabled.ShouldBeFalse(); settings.ForOrg(Guid.NewGuid()).ShouldBeNull();
    }

    // Each refusal below differs from a configuration that is admitted by exactly one thing, so the
    // refusal can only come from the check it names.
    [Fact]
    public void A_complete_simulation_configuration_is_admitted()
    {
        var settings = Read(Simulation());
        (settings.Enabled, settings.Simulated).ShouldBe((true, true));
        settings.ForAccount("sim_fixture")!.Mode.ShouldBe(PaymentModes.Simulation);
    }

    [Fact]
    public void A_complete_stripe_sandbox_configuration_is_admitted_and_its_bindings_carry_the_mode()
    {
        var settings = Read(Sandbox());
        (settings.Enabled, settings.Simulated).ShouldBe((true, false));
        var binding = settings.ForAccount("acct_fixture").ShouldNotBeNull();
        binding.Mode.ShouldBe(PaymentModes.StripeSandbox);
        settings.ForOrg(binding.OrgId).ShouldBe(binding);
    }

    [Fact]
    public void A_restricted_test_key_is_admitted()
    {
        Read(Sandbox(("Payments:Stripe:SecretKey", "rk_test_abc"))).Enabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Stripe_sandbox_is_refused_outside_development(string environment)
    {
        Should.Throw<InvalidOperationException>(() => Read(Sandbox(), environment));
    }

    [Theory]
    [InlineData("sk_live_abc")]
    [InlineData("rk_live_abc")]
    [InlineData("pk_test_abc")]
    [InlineData("sk_test_")]
    [InlineData("SK_TEST_abc")]
    [InlineData("")]
    [InlineData(null)]
    public void Stripe_sandbox_is_refused_without_a_test_mode_secret_key(string? key)
    {
        Should.Throw<InvalidOperationException>(() => Read(Sandbox(("Payments:Stripe:SecretKey", key))));
    }

    [Theory]
    [InlineData("sim_fixture")]
    [InlineData("acct_")]
    [InlineData("fixture")]
    [InlineData("")]
    public void Stripe_sandbox_is_refused_unless_each_fixture_is_bound_to_a_connected_account(string account)
    {
        Should.Throw<InvalidOperationException>(() => Read(Sandbox(("Payments:Fixtures:0:Account", account))));
    }

    [Fact]
    public void Stripe_sandbox_is_refused_with_no_fixture_or_two_fixtures_on_one_account()
    {
        Should.Throw<InvalidOperationException>(() => Read(Sandbox(
            ("Payments:Fixtures:0:OrgId", null), ("Payments:Fixtures:0:Generation", null),
            ("Payments:Fixtures:0:BankId", null), ("Payments:Fixtures:0:Account", null))));
        Should.Throw<InvalidOperationException>(() => Read(Sandbox(
            ("Payments:Fixtures:1:OrgId", Guid.NewGuid().ToString()), ("Payments:Fixtures:1:Generation", Guid.NewGuid().ToString()),
            ("Payments:Fixtures:1:BankId", Guid.NewGuid().ToString()), ("Payments:Fixtures:1:Account", "acct_fixture"))));
    }

    [Fact]
    public void Stripe_sandbox_is_refused_with_provider_configuration_it_does_not_define()
    {
        Should.Throw<InvalidOperationException>(() => Read(Sandbox(("Payments:Stripe:LiveSecretKey", "sk_live_abc"))));
        Should.Throw<InvalidOperationException>(() => Read(Sandbox(("Stripe:SecretKey", "sk_test_abc"))));
    }

    [Fact]
    public void A_simulation_host_refuses_a_stripe_key_and_a_connected_account()
    {
        Should.Throw<InvalidOperationException>(() => Read(Simulation(("Payments:Stripe:SecretKey", "sk_test_abc"))));
        Should.Throw<InvalidOperationException>(() => Read(Simulation(("Payments:Fixtures:0:Account", "acct_fixture"))));
    }

    // A setting is known by its whole path, so nothing rides in beside a fixture or beneath a known key.
    [Theory]
    [InlineData("Payments:Fixtures:0:Mode", "Simulation")]
    [InlineData("Payments:Fixtures:0:ApiKey", "sk_live_abc")]
    [InlineData("Payments:Fixtures:0:Stripe:SecretKey", "sk_live_abc")]
    [InlineData("Payments:SigningKey:Extra", "x")]
    [InlineData("Payments:Stripe:SecretKey:Extra", "x")]
    [InlineData("Payments:Stripe", "sk_live_abc")]
    public void A_setting_nobody_defined_is_refused_wherever_it_sits(string key, string value)
    {
        Should.Throw<InvalidOperationException>(() => Read(Sandbox((key, value))));
        if (!key.StartsWith("Payments:Stripe", StringComparison.Ordinal))
        { Should.Throw<InvalidOperationException>(() => Read(Simulation((key, value)))); }
    }

    [Fact]
    public void A_manifest_that_holds_provider_credentials_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".local");
        try
        {
            File.WriteAllText(path, """{ "Payments": { "Mode": "StripeSandbox", "Stripe": { "SecretKey": "sk_test_abc" } } }""");
            Should.Throw<InvalidOperationException>(() => SimulationSettings.AddManifest(new ConfigurationBuilder(), path));

            File.WriteAllText(path, """{ "Payments": { "Mode": "Disabled" } }""");
            var builder = new ConfigurationBuilder();
            SimulationSettings.AddManifest(builder, path);
            builder.Build()["Payments:Mode"].ShouldBe("Disabled");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_simulator_authenticates_its_own_notices_only_in_a_simulation_host()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var simulator = new SimulatedProcessor(null!, Read(Simulation()), TimeProvider.System);
        simulator.Authenticate(body, simulator.Sign(body)).ShouldNotBeNull();

        var inSandbox = new SimulatedProcessor(null!, Read(Sandbox()), TimeProvider.System);
        inSandbox.Authenticate(body, inSandbox.Sign(body)).ShouldBeNull();
    }

    [Fact]
    public void A_manifest_written_from_the_settings_is_admitted_and_holds_no_stripe_key()
    {
        // As PaymentFixtureBootstrap writes it. A property that leaked into the manifest would either be
        // refused on the next start or put a key in a file.
        var written = JsonSerializer.Serialize(new
        {
            Payments = new SimulationSettings
            {
                Mode = PaymentModes.Simulation,
                SigningKey = new string('x', 64),
                Fixtures = [new FixtureBinding(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "sim_fixture")],
                Stripe = new StripeSandboxSettings { SecretKey = "sk_test_abc" },
            },
        });
        written.ShouldNotContain("sk_test_abc");
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(written))).Build();
        SimulationSettings.Read(config, new EnvironmentStub("Development")).ForAccount("sim_fixture").ShouldNotBeNull();
    }

    [Fact]
    public void A_stripe_sandbox_host_cannot_start_while_it_has_no_processor()
    {
        var services = new ServiceCollection();
        Should.Throw<InvalidOperationException>(() => Read(Sandbox()).AddProcessor(services));
        services.ShouldBeEmpty();

        Read(Simulation()).AddProcessor(services);
        services.ShouldContain(x => x.ServiceType == typeof(IPaymentProcessor));
    }

    private static SimulationSettings Read(Dictionary<string, string?> values, string environment = "Development") =>
        SimulationSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new EnvironmentStub(environment));

    private static Dictionary<string, string?> Simulation(params (string Key, string? Value)[] changes) => With(new()
    {
        ["Payments:Mode"] = "Simulation",
        ["Payments:SigningKey"] = new string('x', 64),
        ["Payments:Fixtures:0:OrgId"] = Guid.NewGuid().ToString(),
        ["Payments:Fixtures:0:Generation"] = Guid.NewGuid().ToString(),
        ["Payments:Fixtures:0:BankId"] = Guid.NewGuid().ToString(),
        ["Payments:Fixtures:0:Account"] = "sim_fixture",
    }, changes);

    private static Dictionary<string, string?> Sandbox(params (string Key, string? Value)[] changes) => With(new()
    {
        ["Payments:Mode"] = "StripeSandbox",
        ["Payments:SigningKey"] = new string('x', 64),
        ["Payments:Stripe:SecretKey"] = "sk_test_abc",
        ["Payments:Fixtures:0:OrgId"] = Guid.NewGuid().ToString(),
        ["Payments:Fixtures:0:Generation"] = Guid.NewGuid().ToString(),
        ["Payments:Fixtures:0:BankId"] = Guid.NewGuid().ToString(),
        ["Payments:Fixtures:0:Account"] = "acct_fixture",
    }, changes);

    // A null value removes the key, so that "absent" is tested as absent and not as empty.
    private static Dictionary<string, string?> With(Dictionary<string, string?> values, (string Key, string? Value)[] changes)
    {
        foreach (var (key, value) in changes)
        {
            if (value is null) { values.Remove(key); } else { values[key] = value; }
        }
        return values;
    }

    private sealed class EnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
