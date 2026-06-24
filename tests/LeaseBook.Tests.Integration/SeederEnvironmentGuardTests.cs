using LeaseBook.Web.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// Security guard: the demo and cutover seeders provision a well-known,
/// source-committed admin password, so they must refuse to run in Production — provisioning either in
/// a reachable environment would be an account-takeover vector. Real orgs come from the M7 onboarding
/// flow. No database is needed: the guard throws before any data access.
/// </summary>
public sealed class SeederEnvironmentGuardTests
{
    [Fact]
    public async Task DemoSeeder_refuses_to_run_in_production()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = BuildServices("Production");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => DemoSeeder.SeedAsync(services, ct));
        ex.Message.ShouldContain("Production");
    }

    [Fact]
    public async Task CutoverSeeder_refuses_to_run_in_production()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = BuildServices("Production");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => CutoverSeeder.SeedAsync(services, ct));
        ex.Message.ShouldContain("Production");
    }

    private static ServiceProvider BuildServices(string environmentName) =>
        new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new StubEnvironment(environmentName))
            .BuildServiceProvider();

    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
