using System.Text;
using System.Text.Json;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.Web.Payments;
using LeaseBook.Web.Payments.Stripe;
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
    // Known by the whole of its name: one with a line break after it is another setting.
    [InlineData("Payments:ManifestPath\n", "x")]
    [InlineData("Payments:Fixtures:0:Account\n", "acct_other")]
    [InlineData("Payments:Stripe:SecretKey\n", "sk_live_abc")]
    [InlineData("Payments:Fixtures:0:CardPaymentMethod\n", "pm_card_visa")]
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
    public void A_manifest_is_written_in_one_shape_whatever_a_binding_holds()
    {
        // Member for member what a simulation manifest has always held. Anything more would be refused
        // as an unknown setting the next time a simulation host read it.
        var written = JsonSerializer.SerializeToNode(new SimulationSettings
        {
            Mode = PaymentModes.Simulation,
            SigningKey = new string('x', 64),
            Fixtures = [new FixtureBinding(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "sim_fixture") { CardPaymentMethod = "pm_card_mastercard" }],
        })!.AsObject();
        written.Select(x => x.Key).ShouldBe(["Mode", "SigningKey", "Fixtures"]);
        written["Fixtures"]![0]!.AsObject().Select(x => x.Key).ShouldBe(["OrgId", "Generation", "BankId", "Account"]);
    }

    [Fact]
    public void A_manifest_for_connected_accounts_is_a_sandbox_manifest_with_one_fixture_each_and_no_key()
    {
        static IConfigurationBuilder Written(params string[] accounts) => new ConfigurationBuilder().AddJsonStream(new MemoryStream(
            JsonSerializer.SerializeToUtf8Bytes(new { Payments = PaymentFixtureBootstrap.Manifest(accounts) })));

        // With no account, the simulation manifest it has always been.
        var simulation = SimulationSettings.Read(Written().Build(), new EnvironmentStub("Development"));
        (simulation.Mode, simulation.Fixtures.Length).ShouldBe((PaymentModes.Simulation, 2));
        simulation.Fixtures.ShouldAllBe(x => x.Account.StartsWith("sim_"));

        // With accounts it names the sandbox and nothing of Stripe's but them, so it starts no host by
        // itself: the key comes from local secrets, and a manifest that held one would be refused.
        Should.Throw<InvalidOperationException>(() => SimulationSettings.Read(Written("acct_1Abc", "acct_2Def").Build(), new EnvironmentStub("Development")))
            .Message.ShouldContain("test-mode secret key");
        var sandbox = SimulationSettings.Read(Written("acct_1Abc", "acct_2Def")
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Payments:Stripe:SecretKey"] = "sk_test_abc" }).Build(), new EnvironmentStub("Development"));
        sandbox.Mode.ShouldBe(PaymentModes.StripeSandbox);
        sandbox.Fixtures.Select(x => x.Account).ShouldBe(["acct_1Abc", "acct_2Def"]);
        sandbox.Fixtures.Select(x => x.OrgId).Distinct().Count().ShouldBe(2);
        sandbox.SigningKey.Length.ShouldBe(64);
    }

    [Fact]
    public void Each_mode_registers_its_own_processor_and_no_other()
    {
        var sandbox = new ServiceCollection().AddSingleton(TimeProvider.System);
        var settings = Read(Sandbox());
        sandbox.AddSingleton(settings);
        settings.AddProcessor(sandbox);
        sandbox.ShouldNotContain(x => x.ServiceType == typeof(SimulatedProcessor));
        using (var provider = sandbox.BuildServiceProvider())
        { provider.GetServices<IPaymentProcessor>().ShouldHaveSingleItem().ShouldBeOfType<StripeSandboxProcessor>(); }

        var simulation = new ServiceCollection();
        Read(Simulation()).AddProcessor(simulation);
        simulation.Count(x => x.ServiceType == typeof(IPaymentProcessor)).ShouldBe(1);
        simulation.ShouldContain(x => x.ServiceType == typeof(SimulatedProcessor));
        simulation.ShouldNotContain(x => x.ServiceType == typeof(StripeSandboxProcessor) || x.ServiceType == typeof(StripeTransport));
    }

    [Fact]
    public void A_sandbox_binding_collects_with_the_test_methods_that_succeed_unless_told_another()
    {
        var binding = Read(Sandbox()).Fixtures.Single();
        (binding.CardPaymentMethod, binding.AchPaymentMethod).ShouldBe(("pm_card_visa", "pm_usBankAccount_success"));

        binding = Read(Sandbox(("Payments:Fixtures:0:CardPaymentMethod", "pm_card_visa_chargeDeclined"),
            ("Payments:Fixtures:0:AchPaymentMethod", "pm_usBankAccount_dispute"))).Fixtures.Single();
        (binding.CardPaymentMethod, binding.AchPaymentMethod).ShouldBe(("pm_card_visa_chargeDeclined", "pm_usBankAccount_dispute"));
        binding.Mode.ShouldBe(PaymentModes.StripeSandbox);
    }

    [Theory]
    [InlineData("CardPaymentMethod", "pm_card_visa")]
    [InlineData("AchPaymentMethod", "pm_usBankAccount_success")]
    public void A_simulation_host_refuses_a_payment_method_setting_by_its_path(string member, string value)
    {
        var refusal = Should.Throw<InvalidOperationException>(() => Read(Simulation(($"Payments:Fixtures:0:{member}", value))));
        refusal.Message.ShouldContain($"Unknown setting: Payments:Fixtures:0:{member}.");
        refusal.Message.ShouldNotContain(value);
        // And nowhere but on a fixture, in a sandbox host either.
        Should.Throw<InvalidOperationException>(() => Read(Sandbox(($"Payments:{member}", value))));
        Should.Throw<InvalidOperationException>(() => Read(Sandbox(($"Payments:Stripe:{member}", value))));
    }

    // Only the shape of Stripe's documented test tokens. A payer's saved method is pm_ and an opaque
    // id, so it is refused here, before anything could be collected with it.
    [Theory]
    [InlineData("CardPaymentMethod", "pm_1QxRealSavedMethod")]
    [InlineData("CardPaymentMethod", "pm_usBankAccount_success")]
    [InlineData("CardPaymentMethod", "pm_card_")]
    [InlineData("CardPaymentMethod", "pm_card_visa\n")]
    [InlineData("CardPaymentMethod", "pm_card_visa&customer=cus_1")]
    [InlineData("CardPaymentMethod", "PM_CARD_visa")]
    [InlineData("CardPaymentMethod", "tok_visa")]
    [InlineData("CardPaymentMethod", "")]
    [InlineData("AchPaymentMethod", "pm_1QxRealSavedMethod")]
    [InlineData("AchPaymentMethod", "pm_card_visa")]
    [InlineData("AchPaymentMethod", "pm_usBankAccount_")]
    [InlineData("AchPaymentMethod", "pm_usbankaccount_success")]
    [InlineData("AchPaymentMethod", "ba_1QxRealBankAccount")]
    [InlineData("AchPaymentMethod", "")]
    public void A_sandbox_host_refuses_a_payment_method_that_is_not_a_documented_test_token(string member, string value)
    {
        var refusal = Should.Throw<InvalidOperationException>(() => Read(Sandbox(($"Payments:Fixtures:0:{member}", value))));
        refusal.Message.ShouldContain($"Payments:Fixtures:0:{member}");
        refusal.Message.ShouldContain("test payment methods");
        // Named by its path, not echoed: the refusal's own text names the two prefixes and no more.
        if (value.Length > 8 && !value.EndsWith('_')) { refusal.Message.ShouldNotContain(value); }
    }

    [Theory]
    [InlineData]
    [InlineData("--stripe-account=acct_1AbcDEF")]
    [InlineData("--stripe-account=acct_1AbcDEF", "--stripe-account=acct_2AbcDEF")]
    public void Init_takes_a_manifest_and_any_number_of_connected_accounts(params string[] accounts)
    {
        new PaymentSimulationVerb().TryCreateInvocation(["payment-simulation", "init", "fixture.local", .. accounts], out var invocation, out var error)
            .ShouldBeTrue(error);
        invocation.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("--stripe-account=sim_fixture")]
    [InlineData("--stripe-account=acct_")]
    [InlineData("--stripe-account=")]
    [InlineData("--stripe-account=acct_1Abc\",\"Stripe\":{}")]
    [InlineData("--stripe-account=acct_1Abc DEF")]
    [InlineData("--stripe-account=acct_1AbcDEF", "--stripe-account=acct_1AbcDEF")]
    [InlineData("--stripe-key=sk_test_abc")]
    [InlineData("acct_1AbcDEF")]
    [InlineData("--stripe-account", "acct_1AbcDEF")]
    public void Init_refuses_anything_but_distinct_connected_account_ids(params string[] accounts)
    {
        new PaymentSimulationVerb().TryCreateInvocation(["payment-simulation", "init", "fixture.local", .. accounts], out _, out var error)
            .ShouldBeFalse();
        error.ShouldContain("--stripe-account=<acct_...>");
        // Told what is wrong, never shown it back: an argument here could be a key given by mistake.
        foreach (var given in new[] { "sk_test_abc", "1Abc", "sim_fixture" }) { error.ShouldNotContain(given); }
    }

    [Fact]
    public void Init_refuses_an_account_id_longer_than_a_binding_holds_and_a_manifest_that_is_not_local()
    {
        var verb = new PaymentSimulationVerb();
        verb.TryCreateInvocation(["payment-simulation", "init", "fixture.local", "--stripe-account=acct_" + new string('a', 95)], out _, out _).ShouldBeTrue();
        verb.TryCreateInvocation(["payment-simulation", "init", "fixture.local", "--stripe-account=acct_" + new string('a', 96)], out _, out _).ShouldBeFalse();
        verb.TryCreateInvocation(["payment-simulation", "init", "fixture.json", "--stripe-account=acct_1AbcDEF"], out _, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("emit")]
    [InlineData("payout")]
    public async Task The_simulators_drivers_refuse_outside_simulation_instead_of_failing_on_a_missing_simulator(string verb)
    {
        var ct = TestContext.Current.CancellationToken;
        string[] args = verb == "emit"
            ? ["payment-simulation", "emit", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "BankCredit", "2026-10-08"]
            : ["payment-simulation", "payout", Guid.NewGuid().ToString(), "2026-10-08", "po_1", "pay:" + Guid.NewGuid()];
        new PaymentSimulationVerb().TryCreateInvocation(args, out var invocation, out var error).ShouldBeTrue(error);

        // Nothing but the settings is registered: a driver that went on to anything else would throw
        // on the missing service, which is the failure this replaces.
        foreach (var settings in new[] { Read(Sandbox()), Read(new() { ["Payments:Mode"] = "Disabled" }) })
        {
            await using var services = new ServiceCollection().AddSingleton(settings).BuildServiceProvider();
            (await invocation.RunAsync(services, ct)).ShouldBe(1);
        }

        // In a simulation host it goes on, here as far as the organization nobody bound.
        await using var simulation = new ServiceCollection().AddSingleton(Read(Simulation())).BuildServiceProvider();
        await Should.ThrowAsync<PaymentUnavailableException>(() => invocation.RunAsync(simulation, ct));
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
