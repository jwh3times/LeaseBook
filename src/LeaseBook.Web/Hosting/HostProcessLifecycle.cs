using Azure.Extensions.AspNetCore.DataProtection.Keys;
using Hangfire;
using Hangfire.PostgreSql;
using LeaseBook.Web.Adapters;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Capabilities;
using LeaseBook.Web.Cli;
using LeaseBook.Web.Jobs;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

namespace LeaseBook.Web.Hosting;

/// <summary>
/// Selects and activates the one lifecycle this executable is running. Process-mode policy belongs
/// here so the composition root does not have to reproduce a matrix of CLI, OpenAPI-build, and Web
/// exclusions around each startup side effect (ADR-042).
/// </summary>
internal sealed class HostProcessLifecycle
{
    private readonly CliInvocation? _cliInvocation;
    private ForwardedHeadersSettings _forwardedHeaders = new();
    private bool _jobsEnabled;
    private bool _configured;

    internal HostProcessLifecycle(HostProcessMode mode, CliInvocation? cliInvocation = null)
    {
        if ((mode == HostProcessMode.Cli) != (cliInvocation is not null))
        {
            throw new ArgumentException("CLI mode requires exactly one resolved CLI invocation.", nameof(cliInvocation));
        }

        Mode = mode;
        _cliInvocation = cliInvocation;
    }

    internal HostProcessMode Mode { get; }

    /// <summary>
    /// Resolves argv before host composition. The OpenAPI build flag and a recognized CLI verb are
    /// mutually exclusive; accepting both would recreate the hybrid policy this module removes.
    /// </summary>
    internal static HostProcessResolution Resolve(string[] args, bool isOpenApiBuild)
    {
        var cli = CliApplication.Resolve(args);

        if (cli.IsCli && isOpenApiBuild)
        {
            return HostProcessResolution.Failure(
                "A LeaseBook CLI verb cannot run while LEASEBOOK_OPENAPI_BUILD=1. " +
                "Choose either foreground CLI execution or build-time OpenAPI generation.");
        }

        if (cli.Error is { } cliError)
        {
            return HostProcessResolution.Failure(cliError);
        }

        if (cli.Invocation is { } invocation)
        {
            return HostProcessResolution.Success(new HostProcessLifecycle(HostProcessMode.Cli, invocation));
        }

        return HostProcessResolution.Success(new HostProcessLifecycle(
            isOpenApiBuild ? HostProcessMode.OpenApiBuild : HostProcessMode.Web));
    }

    /// <summary>
    /// Adds only infrastructure whose configuration differs by process mode. Application modules and
    /// callable core services remain in the shared graph assembled by <c>Program</c>.
    /// </summary>
    internal void Configure(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (_configured)
        {
            throw new InvalidOperationException("The host process lifecycle has already been configured.");
        }

        _configured = true;

        if (Mode == HostProcessMode.Cli)
        {
            // Foreground verbs own their operator-facing error. Suppress EF's earlier duplicate
            // error logs without changing Web diagnostics (ADR-025).
            builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
            builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Update", LogLevel.None);
        }

        ConfigureDataProtection(builder);

        if (Mode != HostProcessMode.Web)
        {
            return;
        }

        ConfigureForwardedHeaders(builder);
        ConfigureWebWorkers(builder);
        ConfigureScheduledJobs(builder);
    }

    /// <summary>
    /// Runs the selected lifecycle. CLI execution intentionally precedes HTTP-pipeline construction;
    /// Web and OpenAPI modes receive the same mode-neutral pipeline definition.
    /// </summary>
    internal async Task<int?> RunAsync(
        WebApplication app,
        Action<WebApplication> configureHttpPipeline,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configureHttpPipeline);
        if (!_configured)
        {
            throw new InvalidOperationException("Configure must be called before the host lifecycle runs.");
        }

        if (Mode == HostProcessMode.Cli)
        {
            return await _cliInvocation!.RunAsync(app.Services, ct);
        }

        if (Mode == HostProcessMode.Web)
        {
            LogProductionConfigurationWarnings(app);
        }

        // Must precede the rate limiter in the shared pipeline: its partition reads RemoteIpAddress.
        if (_forwardedHeaders.Enabled)
        {
            app.UseForwardedHeaders();
        }

        configureHttpPipeline(app);

        if (Mode == HostProcessMode.Web)
        {
            await PrepareWebHostAsync(app, ct);
        }

        await app.RunAsync(ct);
        return null;
    }

    private void ConfigureDataProtection(WebApplicationBuilder builder)
    {
        var dataProtection = builder.Services.AddDataProtection()
            // Never derive this from content root: revisions must be able to decrypt one another's
            // Identity token rows after a container replacement (ADR-041).
            .SetApplicationName("LeaseBook");

        // The build-time OpenAPI tool executes the host with no database or deployment identity.
        // Its document needs the service registration, not the durable production keyring.
        if (Mode == HostProcessMode.OpenApiBuild)
        {
            return;
        }

        dataProtection.PersistKeysToDbContext<KeyringDbContext>();

        var keyVaultKeyUri = builder.Configuration["DataProtection:KeyVaultKeyUri"];
        if (!string.IsNullOrWhiteSpace(keyVaultKeyUri))
        {
            dataProtection.ProtectKeysWithAzureKeyVault(
                new Uri(keyVaultKeyUri), new Azure.Identity.DefaultAzureCredential());
        }
    }

    private void ConfigureForwardedHeaders(WebApplicationBuilder builder)
    {
        _forwardedHeaders = builder.Configuration
            .GetSection(ForwardedHeadersSettings.SectionName)
            .Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        var (knownProxies, knownNetworks) = _forwardedHeaders.Resolve();

        if (!_forwardedHeaders.Enabled)
        {
            return;
        }

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = _forwardedHeaders.ForwardLimit;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var proxy in knownProxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach (var network in knownNetworks)
            {
                options.KnownIPNetworks.Add(network);
            }
        });
    }

    private static void ConfigureWebWorkers(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<CapabilityNotificationListener>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CapabilityNotificationListener>());
        builder.Services.AddSingleton<CapabilityReadinessProbe>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CapabilityReadinessProbe>());
        builder.Services.AddSingleton<RoleSeedingProbe>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RoleSeedingProbe>());
    }

    private void ConfigureScheduledJobs(WebApplicationBuilder builder)
    {
        _jobsEnabled = builder.Configuration.GetValue<bool>("Jobs:Enabled");
        if (!_jobsEnabled)
        {
            return;
        }

        builder.Services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                postgres => postgres.UseNpgsqlConnection(builder.Configuration.GetConnectionString("Default")),
                new PostgreSqlStorageOptions
                {
                    // The runtime role owns this one schema because Hangfire upgrades its own
                    // objects; changing ownership to the migrator breaks package upgrades (ADR-001).
                    SchemaName = "hangfire",
                    PrepareSchemaIfNecessary = true,
                    // A server without its invariant-sweep storage must fail rather than start in a
                    // mode that looks healthy while silently doing no scheduled work.
                    AllowDegradedModeWithoutStorage = false,
                }));

        builder.Services.AddHangfireServer();
        builder.Services.AddScoped<InvariantSweepJob>();
    }

    private void LogProductionConfigurationWarnings(WebApplication app)
    {
        if (!app.Environment.IsProduction())
        {
            return;
        }

        var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        if (string.IsNullOrWhiteSpace(app.Configuration["DataProtection:KeyVaultKeyUri"]))
        {
            startupLog.LogWarning(
                "DataProtection:KeyVaultKeyUri is not set. The keyring is durable (it persists to " +
                "Postgres) but its key material is unwrapped, so it sits in the same database as the " +
                "data it protects. Set the Key Vault key URI before go-live.");
        }

        if (!_forwardedHeaders.Enabled)
        {
            startupLog.LogWarning(
                "{Section}:Enabled is false in Production. Per-client rate-limit partitioning falls back " +
                "to the connection address, which behind an ingress proxy is the proxy — one shared " +
                "partition for every client. Name the ingress before go-live.",
                ForwardedHeadersSettings.SectionName);
        }
    }

    private async Task PrepareWebHostAsync(WebApplication app, CancellationToken ct)
    {
        // An unreachable database is tolerated so Kestrel can bind and readiness can report 503.
        // RoleSeeder rethrows structural faults, and the Web-only retry probe advances the state
        // after a transient outage (ADR-028).
        if (await RoleSeeder.TryEnsureRolesAsync(app.Services, ct))
        {
            app.Services.GetRequiredService<RoleSeedingState>().MarkSeeded();
        }

        ProductionSecurityGuards.Validate(app.Configuration, app.Environment);
        await CapabilityRegistryValidator.ValidateAsync(app.Services, app.Environment, ct);

        if (_jobsEnabled)
        {
            // Idempotent by job id: a cron change updates the one schedule rather than accumulating
            // duplicate nightly sweeps.
            app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<InvariantSweepJob>(
                InvariantSweepJob.JobId,
                job => job.RunAsync(CancellationToken.None),
                InvariantSweepJob.CronUtc,
                new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        }
    }
}

internal enum HostProcessMode
{
    Web,
    Cli,
    OpenApiBuild,
}

internal sealed record HostProcessResolution(HostProcessLifecycle? Lifecycle, string? Error)
{
    internal static HostProcessResolution Success(HostProcessLifecycle lifecycle) => new(lifecycle, null);

    internal static HostProcessResolution Failure(string error) => new(null, error);
}
