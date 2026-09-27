using LeaseBook.Web.Payments;
using Microsoft.Extensions.Configuration;
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

    private sealed class EnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
