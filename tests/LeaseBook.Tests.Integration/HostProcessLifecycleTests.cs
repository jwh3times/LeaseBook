using LeaseBook.Web.Adapters;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Cli;
using LeaseBook.Web.Hosting;
using LeaseBook.Web.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// ADR-042's process-mode interface. These tests observe selection, registration, and activation
/// through the lifecycle rather than pinning where its implementation happens to live.
/// </summary>
public sealed class HostProcessLifecycleTests
{
    [Fact]
    public void Web_cli_and_openapi_build_resolve_as_exclusive_modes()
    {
        HostProcessLifecycle.Resolve([], isOpenApiBuild: false)
            .Lifecycle!.Mode.ShouldBe(HostProcessMode.Web);
        HostProcessLifecycle.Resolve(["perf-probe"], isOpenApiBuild: false)
            .Lifecycle!.Mode.ShouldBe(HostProcessMode.Cli);
        HostProcessLifecycle.Resolve([], isOpenApiBuild: true)
            .Lifecycle!.Mode.ShouldBe(HostProcessMode.OpenApiBuild);
    }

    [Fact]
    public void A_cli_openapi_hybrid_is_rejected_before_host_composition()
    {
        var resolution = HostProcessLifecycle.Resolve(["perf-probe"], isOpenApiBuild: true);

        resolution.Lifecycle.ShouldBeNull();
        resolution.Error.ShouldNotBeNull();
        resolution.Error.ShouldContain("cannot run while LEASEBOOK_OPENAPI_BUILD=1");
    }

    [Fact]
    public void A_recognized_cli_usage_error_remains_an_early_terminal_result()
    {
        var resolution = HostProcessLifecycle.Resolve(["seed"], isOpenApiBuild: false);

        resolution.Lifecycle.ShouldBeNull();
        resolution.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Only_web_mode_registers_startable_workers_and_scheduled_job_infrastructure()
    {
        var web = Configure(HostProcessMode.Web, jobsEnabled: true);
        var cli = Configure(
            HostProcessMode.Cli,
            jobsEnabled: true,
            new CliInvocation("test", (_, _) => Task.FromResult(CliExitCodes.Success)));
        var openApi = Configure(HostProcessMode.OpenApiBuild, jobsEnabled: true);

        AssertWebOnlyInfrastructure(web.Services, expected: true);
        AssertWebOnlyInfrastructure(cli.Services, expected: false);
        AssertWebOnlyInfrastructure(openApi.Services, expected: false);

        var webHostedServices = web.Services.Count(service => service.ServiceType == typeof(IHostedService));
        webHostedServices.ShouldBeGreaterThan(
            cli.Services.Count(service => service.ServiceType == typeof(IHostedService)));
        webHostedServices.ShouldBeGreaterThan(
            openApi.Services.Count(service => service.ServiceType == typeof(IHostedService)));
    }

    [Fact]
    public async Task Cli_runs_its_invocation_without_constructing_the_http_pipeline()
    {
        var invoked = false;
        var pipelineConfigured = false;
        var lifecycle = new HostProcessLifecycle(
            HostProcessMode.Cli,
            new CliInvocation("test", (_, _) =>
            {
                invoked = true;
                return Task.FromResult(CliExitCodes.Unavailable);
            }));
        var builder = Builder(jobsEnabled: true);
        builder.Configuration["ForwardedHeaders:Enabled"] = "true";
        lifecycle.Configure(builder);
        await using var app = builder.Build();

        var exitCode = await lifecycle.RunAsync(
            app,
            _ => pipelineConfigured = true,
            TestContext.Current.CancellationToken);

        invoked.ShouldBeTrue();
        pipelineConfigured.ShouldBeFalse();
        exitCode.ShouldBe(CliExitCodes.Unavailable);
    }

    /// <summary>
    /// Minting the sign-in decoy hash costs one PBKDF2 hash — tens of milliseconds — and nothing a
    /// CLI verb does can serve a sign-in, so a verb that mints one has paid for a value it will never
    /// read (#367). The warm-up is deliberately eager for the Web host; the fix is to scope it, not
    /// to defer it, which <c>LoginTimingTests</c> pins from the other side.
    /// <para>
    /// What this <b>cannot</b> see is the regression #367 actually reported: it drives a lifecycle it
    /// composed itself, so a warm-up in the composition root would leave it green. What it pins is
    /// that the CLI branch returns before the Web startup step the warm-up now lives in. The call
    /// site is <c>SignInTimingWarmupTests</c>' subject, and the two are only jointly sufficient.
    /// </para>
    /// <para>
    /// A stub hasher stands in because the mode-neutral graph this builder composes has no Identity:
    /// without it a regression would surface as a resolution failure rather than as this assertion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_cli_run_mints_no_decoy_password_hash()
    {
        var hasher = new CountingPasswordHasher();
        var lifecycle = new HostProcessLifecycle(
            HostProcessMode.Cli,
            new CliInvocation("test", (_, _) => Task.FromResult(CliExitCodes.Success)));
        var builder = Builder(jobsEnabled: false);
        builder.Services.AddSingleton<PasswordTimingEqualizer>();
        builder.Services.AddSingleton<IPasswordHasher<AppUser>>(hasher);
        lifecycle.Configure(builder);
        await using var app = builder.Build();

        var exitCode = await lifecycle.RunAsync(app, _ => { }, TestContext.Current.CancellationToken);

        // Guards the guard: a lifecycle that silently stopped running the invocation would also
        // never warm, and would satisfy the assertions below having exercised nothing.
        exitCode.ShouldBe(CliExitCodes.Success);
        hasher.Hashes.ShouldBe(
            0, "a CLI verb cannot serve a sign-in, so minting the decoy hash is pure startup cost");
        app.Services.GetRequiredService<PasswordTimingEqualizer>().IsWarm.ShouldBeFalse();
    }

    [Fact]
    public async Task Openapi_build_composes_without_a_database_or_deployment_configuration()
    {
        var lifecycle = new HostProcessLifecycle(HostProcessMode.OpenApiBuild);
        var builder = Builder(jobsEnabled: true);
        builder.Configuration["ConnectionStrings:Default"] = null;
        builder.Configuration["AllowedHosts"] = null;
        builder.Configuration["ForwardedHeaders:Enabled"] = "true";

        lifecycle.Configure(builder);

        await using var app = builder.Build();
    }

    private static WebApplicationBuilder Configure(
        HostProcessMode mode,
        bool jobsEnabled,
        CliInvocation? invocation = null)
    {
        var lifecycle = new HostProcessLifecycle(mode, invocation);
        var builder = Builder(jobsEnabled);
        lifecycle.Configure(builder);
        return builder;
    }

    private static WebApplicationBuilder Builder(bool jobsEnabled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jobs:Enabled"] = jobsEnabled.ToString(),
            ["ForwardedHeaders:Enabled"] = "false",
        });
        return builder;
    }

    private static void AssertWebOnlyInfrastructure(IServiceCollection services, bool expected)
    {
        Has<CapabilityNotificationListener>(services).ShouldBe(expected);
        Has<CapabilityReadinessProbe>(services).ShouldBe(expected);
        Has<RoleSeedingProbe>(services).ShouldBe(expected);
        Has<InvariantSweepJob>(services).ShouldBe(expected);
    }

    private static bool Has<T>(IServiceCollection services) =>
        services.Any(service => service.ServiceType == typeof(T));

    private sealed class CountingPasswordHasher : IPasswordHasher<AppUser>
    {
        public int Hashes { get; private set; }

        public string HashPassword(AppUser user, string password)
        {
            Hashes++;
            return "stub";
        }

        public PasswordVerificationResult VerifyHashedPassword(
            AppUser user, string hashedPassword, string providedPassword) =>
            PasswordVerificationResult.Failed;
    }
}
