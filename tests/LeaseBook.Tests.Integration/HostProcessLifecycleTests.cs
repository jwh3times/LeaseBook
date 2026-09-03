using LeaseBook.Web.Adapters;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Cli;
using LeaseBook.Web.Hosting;
using LeaseBook.Web.Jobs;
using Microsoft.AspNetCore.Builder;
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
}
