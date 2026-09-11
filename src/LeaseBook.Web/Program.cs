using System.Reflection;
using System.Threading.RateLimiting;
using Azure.Monitor.OpenTelemetry.Exporter;
using LeaseBook.Modules.Accounting;
using LeaseBook.Modules.Banking;
using LeaseBook.Modules.Capabilities;
using LeaseBook.Modules.Directory;
using LeaseBook.Modules.Operations;
using LeaseBook.Modules.Reporting;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.SharedKernel.Observability;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Adapters;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Cli;
using LeaseBook.Web.Endpoints;
using LeaseBook.Web.Health;
using LeaseBook.Web.Hosting;
using LeaseBook.Web.Jobs;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Reporting;
using LeaseBook.Web.Security;
using LeaseBook.Web.Tenancy;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using QuestPDF.Infrastructure;

// QuestPDF Community license (M5 WP-04). Free for organizations under the $1M annual revenue
// threshold; LeaseBook qualifies at launch. Must be set before the first document is rendered.
QuestPDF.Settings.License = LicenseType.Community;

// Select exactly one process lifecycle before composing the host (ADR-042). Parsing is pure, so a
// CLI usage error or an invalid CLI/OpenAPI hybrid fails before configuration or the database can.
var process = HostProcessLifecycle.Resolve(
    args,
    Environment.GetEnvironmentVariable("LEASEBOOK_OPENAPI_BUILD") == "1");
if (process.Error is { } processError)
{
    Console.Error.WriteLine(processError);
    Environment.ExitCode = CliExitCodes.Failure;
    return;
}

var lifecycle = process.Lifecycle!;
var builder = WebApplication.CreateBuilder(args);

// Module assemblies the host composes. CQRS handlers/validators are discovered from these; endpoint
// modules are discovered from these plus the host (which owns the auth/meta endpoints).
Assembly[] moduleAssemblies =
[
    typeof(LeaseBook.Modules.Accounting.ModuleMarker).Assembly,
    typeof(LeaseBook.Modules.Directory.ModuleMarker).Assembly,
    typeof(LeaseBook.Modules.Banking.ModuleMarker).Assembly,
    typeof(LeaseBook.Modules.Reporting.ModuleMarker).Assembly,
    typeof(LeaseBook.Modules.Operations.ModuleMarker).Assembly,
    typeof(LeaseBook.Modules.Payments.ModuleMarker).Assembly,
];
Assembly[] endpointAssemblies = [.. moduleAssemblies, typeof(Program).Assembly];

builder.Services.AddLeaseBookCqrs(moduleAssemblies);

// RFC 7807 everywhere (P17): ProblemDetails defaults + the CQRS ValidationException → 400 mapping.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
// Typed accounting domain errors → §C.5 ProblemDetails (422/409). Wired now so M3's write path inherits it.
builder.Services.AddExceptionHandler<AccountingExceptionHandler>();
// Typed Operations run-pipeline errors → 409 ProblemDetails, keyed on the code the exception
// carries (ADR-028): capabilities_changed for a preview whose set moved, and
// capabilities_changed_since_prior_run for a period an earlier run computed under a different
// money-path state. Typed so neither falls through to the terminal handler's uncoded 500, which
// would turn a recoverable rejection into an opaque failure.
builder.Services.AddExceptionHandler<OperationsExceptionHandler>();
// Terminal handler — MUST stay last. Handlers run in registration order; this one claims
// everything the typed handlers decline, so nothing reaches the framework default (a bodyless
// 500 with no log).
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();

// Data access (runtime = app role, RLS-subject). Migrations use the migrator connection via the
// design-time factory; the running app never connects as migrator.
builder.Services.AddDbContext<AppDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Default"),
        npgsql => npgsql.SetPostgresVersion(18, 0))
    .UseSnakeCaseNamingConvention());

// Identity, cookie auth, antiforgery, deny-by-default authorization (P12).
builder.Services.AddLeaseBookIdentity(builder.Environment);

// F6/F8 (ADR-041): the Identity token-store keyring uses a separate context; the process lifecycle
// decides whether this run may activate its durable Postgres/Key Vault configuration (ADR-042).
builder.Services.AddDbContext<KeyringDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Default"),
        npgsql => npgsql.SetPostgresVersion(18, 0))
    .UseSnakeCaseNamingConvention());

// WP-5 F3b: config-gated MFA enforcement for PMAdmin (default off; Production turns it on).
builder.Services.Configure<LeaseBook.Web.Auth.AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    LeaseBook.Web.Security.MfaEnrolledAuthorizationHandler>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler,
    LeaseBook.Web.Security.MfaAuthorizationResultHandler>();

// Per-IP auth rate limiting (WP-5): "auth" policy applied to login + mfa only (Task 4). Limits are
// configurable per environment — generous in Development/tests (appsettings.json), strict in
// Production (appsettings.Production.json) — so the shared TestServer "unknown" IP partition is
// never tripped by unrelated tests.
builder.Services.Configure<RateLimitingOptions>(builder.Configuration.GetSection("RateLimiting"));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext =>
    {
        var rateLimiting = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimiting.AuthPermitLimit,
                Window = TimeSpan.FromSeconds(rateLimiting.AuthWindowSeconds),
                QueueLimit = 0,
            });
    });
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    };
});

// Organization-isolation ergonomics: one request-scoped OrgContext, exposed read-only as IOrgContext (which
// the DbContext query filter reads). DbContext is also resolvable as its base type so the
// scheduler-agnostic OrgScopedExecutor can open the unit-of-work transaction.
builder.Services.AddScoped<OrgContext>();
builder.Services.AddScoped<IOrgContext>(sp => sp.GetRequiredService<OrgContext>());
// Actor context (P52): the auth middleware populates it from the user-id claim; PostingService and the
// AppDbContext audit pass read it to stamp created_by / actor_user_id. Null for seeder/job writes.
builder.Services.AddScoped<ActorContext>();
builder.Services.AddScoped<IActorContext>(sp => sp.GetRequiredService<ActorContext>());
builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<OrgScopedExecutor>();
// Platform-plane counterpart (ADR-028): the single call site that sets app.platform. Scoped, like
// OrgScopedExecutor, because it opens a transaction on the request/job-scoped DbContext.
builder.Services.AddScoped<PlatformScopedExecutor>();

// Capability seam (ADR-028): the module contributes ICapabilityGate — the single seam every caller
// uses — over the per-replica cache and its state reader. The IPlatformScope port is host-implemented
// (ADR-007), since the module cannot name PlatformScopedExecutor; the gate deliberately does not
// consume it, because the money path resolves inside the request transaction that OrgContextMiddleware
// has already opened.
builder.Services.AddCapabilitiesModule();
builder.Services.AddScoped<LeaseBook.Modules.Capabilities.Contracts.IPlatformScope, PlatformScopeAdapter>();
// Identity is host-owned, so "is this user in this org?" is also a port (ADR-007). asp_net_users is
// RLS-exempt, which makes this the only thing stopping a cohort rule naming another organization's user.
builder.Services.AddScoped<LeaseBook.Modules.Capabilities.Contracts.IOrgMembership, OrgMembershipAdapter>();

// Readiness state is part of the shared graph. The Web-only lifecycle supplies the background probe
// that can advance it after a transient database outage (ADR-028 / ADR-042).
builder.Services.AddSingleton<RoleSeedingState>();

// Readiness (Task 7): what the probes above establish, /api/health/ready reports. Registered
// unconditionally — each check only reads a bool off a singleton, so they cost nothing in the OpenAPI
// build, and MetaEndpoints maps the route in every configuration. Tagged `ready` so liveness
// (/api/health) and readiness stay separable: an unreachable seam must remove a replica from
// rotation, never restart it.
//
// TWO checks, not one, and both are load-bearing. The seam being reachable says nothing about whether
// the four fixed roles exist — seeding happens once at boot, reachability is proven continuously — so
// a replica that rode out a database outage at boot would otherwise report healthy with no roles and
// fail every authenticated request. The endpoint reports the worst of the two.
builder.Services.AddHealthChecks()
    .AddCheck<CapabilityReadinessCheck>(CapabilityReadinessCheck.Name, tags: [CapabilityReadinessCheck.ReadyTag])
    .AddCheck<RoleSeedingReadinessCheck>(RoleSeedingReadinessCheck.Name, tags: [CapabilityReadinessCheck.ReadyTag]);

// Accounting module services (chart-of-accounts provisioning, period lifecycle; the posting engine
// and event catalog register here in later WPs). They consume the ambient DbContext + IOrgContext.
builder.Services.AddAccountingModule();

// Directory module services (settings/bank/fee config; CQRS handlers are auto-discovered). The host
// implements Directory's cross-module ports with thin adapters (ADR-007 / P49): IChartProvisioner
// delegates bank-account provisioning to the Accounting chart-of-accounts.
builder.Services.AddDirectoryModule();
builder.Services.AddScoped<LeaseBook.Modules.Directory.Contracts.IChartProvisioner, ChartProvisionerAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Directory.Contracts.ITenantFinancials, TenantFinancialsAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Directory.Contracts.IOwnerFinancials, OwnerFinancialsAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Directory.Contracts.IPropertyDepositTransfer, PropertyDepositTransferAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Directory.Contracts.IBankClearanceStatus, BankClearanceStatusAdapter>();

// The reverse seam (M3 / P58): the Accounting ledger composer resolves a tenant's owner/property/unit
// from the active lease through Directory. Accounting owns the port; the host adapter delegates via ISender.
builder.Services.AddScoped<LeaseBook.Modules.Accounting.Contracts.ITenantPostingDimensions, TenantPostingDimensionsAdapter>();

// M5 WP-01 (ADR-016): Accounting owns the statement engine; the Reporting module consumes it via this port.
builder.Services.AddScoped<LeaseBook.Modules.Accounting.Contracts.IOwnerStatementData, OwnerStatementDataAdapter>();

// M5 WP-03 (ADR-016): Reporting module ports — owner/property names, PM branding, reconciliation snapshots.
// All three are host adapters that dispatch to Directory/Accounting queries via ISender.
builder.Services.AddScoped<LeaseBook.Modules.Reporting.Contracts.IStatementNames, StatementNamesAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Reporting.Contracts.IPmBranding, PmBrandingAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Reporting.Contracts.IReconciliationSnapshots, ReconciliationSnapshotsAdapter>();

// Reporting module services (CQRS handlers auto-discovered; no module-level services yet).
builder.Services.AddReportingModule();

// Host-composed reporting services — StatementAssembler and ReportPreviewService cross module
// boundaries via ISender (composition root pattern, same as DashboardService).
builder.Services.AddScoped<StatementAssembler>();
builder.Services.AddScoped<ReportPreviewService>();
// WP-8: the trust-compliance pack composes existing reads (ISender) + the host audit extract.
builder.Services.AddScoped<CompliancePackAssembler>();

// M5 WP-05: statement delivery seam + artifact store. IArtifactStore is the byte-only store
// (local = file system; M8 = Azure Blob). IStatementDelivery is host-owned (references StatementPdf
// / StatementView). Both are scoped — DeliveryRecord insert needs the ambient organization context.
builder.Services.AddScoped<LeaseBook.Modules.Reporting.Delivery.IArtifactStore,
    LeaseBook.Modules.Reporting.Delivery.LocalArtifactStore>();
builder.Services.AddScoped<IStatementDelivery, LocalStatementDelivery>();

// Operations module services (run engine; CQRS handlers are auto-discovered). The host implements
// Operations' cross-module ports (ADR-007 / ADR-019):
//   IBatchPosting — write-direction: translates run intents into IAccountingEvents.PostAsync calls.
//   ILeaseScheduleData — read-direction: dispatches Directory's GetActiveLeaseSchedule via ISender.
//   IPostedSourceRefs — read-direction: dispatches Accounting's GetExistingSourceRefs via ISender.
//   ICapabilitySnapshot — read-direction: maps ICapabilityGate's durable resolve into Operations'
//     own RunCapabilities view, on the ambient transaction (ADR-028).
builder.Services.AddOperationsModule();
builder.Services.AddScoped<LeaseBook.Modules.Operations.Contracts.IBatchPosting, BatchPostingAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Operations.Contracts.ICapabilitySnapshot, CapabilitySnapshotAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Operations.Contracts.ILeaseScheduleData, LeaseScheduleDataAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Operations.Contracts.IPostedSourceRefs, PostedSourceRefsAdapter>();
// WP-3: Late-fee run ports — policy resolution and delinquency signal (ADR-007 / WP-3).
builder.Services.AddScoped<LeaseBook.Modules.Operations.Contracts.ILateFeePolicyData, LateFeePolicyDataAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Operations.Contracts.IDelinquencyData, DelinquencyDataAdapter>();

// Fix A (M6 final): IPeriodChargeGuard — structural cross-source double-charge guard (ADR-007).
// Detects charges posted by any means (manual, seed, import) in a period, not just bulk-run keys.
builder.Services.AddScoped<LeaseBook.Modules.Operations.Contracts.IPeriodChargeGuard, PeriodChargeGuardAdapter>();

// WP-4: Disbursement run ports — owner data, equity balances, bank account info (ADR-018).
builder.Services.AddScoped<LeaseBook.Modules.Operations.Contracts.IOwnerDisbursementData, OwnerDisbursementDataAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Operations.Contracts.IOwnerEquityBalances, OwnerEquityBalancesAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Operations.Contracts.IBankAccountInfo, BankAccountInfoAdapter>();

// Banking module services (CSV import/match; CQRS handlers are auto-discovered). The host implements
// Banking's cross-module ports with thin adapters (ADR-007 / P68): IBankRegister reads uncleared register
// lines and IBankClearing applies clearances, both delegating to Accounting via ISender.
builder.Services.AddBankingModule();
builder.Services.AddScoped<LeaseBook.Modules.Banking.Contracts.IBankRegister, BankRegisterAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Banking.Contracts.IBankClearing, BankClearingAdapter>();

// M7 WP-3: onboarding import services + external-id resolver. Host-owned (composition root).
// EntityImportService (3.1): reads across Directory commands via ISender; persists staging rows.
// BalanceImportService (3.2): posts opening positions via IBalanceForward; persists staging rows.
builder.Services.AddScoped<LeaseBook.Web.Onboarding.ExternalIdResolver>();
builder.Services.AddScoped<LeaseBook.Web.Onboarding.EntityImportService>();
builder.Services.AddScoped<LeaseBook.Web.Onboarding.MigrationCutoverDate>();
builder.Services.AddScoped<LeaseBook.Web.Onboarding.BalanceImportService>();

// M7 WP-4: verification + sign-off. VerificationService dispatches the Accounting
// IMigrationVerificationData query via ISender and enforces the tie-out gate.
builder.Services.AddScoped<LeaseBook.Web.Onboarding.Verification.VerificationService>();

// Host-composed dashboard (§C.6 / P45): the cross-module composition root, dispatching module read
// queries via ISender. TimeProvider drives the "current accounting month" (injectable for tests).
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<LeaseBook.Web.Dashboard.DashboardService>();

// Host-composed per-entry audit trail (P56): joins host audit/identity tables with the Accounting
// reversal link, resolving actors via an org-filtered identity lookup (the soft-spot has no RLS).
builder.Services.AddScoped<LeaseBook.Web.Audit.EntryAuditReader>();
// WP-8: the period-scoped money-touching audit extract for the trust-compliance pack.
builder.Services.AddScoped<LeaseBook.Web.Audit.AuditExtractReader>();

// OpenAPI document (P11) — the SPA's `npm run api:generate` reads /openapi/v1.json.
builder.Services.AddOpenApi();

// Telemetry baseline: emit the CQRS ActivitySource (+ request spans). The Azure Monitor exporter
// is added only when a connection string is present, so locally this collects nothing (no-op).
var telemetry = builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .ConfigureResource(resource => resource.AddService("LeaseBook.Web"))
        .AddSource(LeaseBookTelemetry.SourceName)
        .AddAspNetCoreInstrumentation());

// ILogger → OpenTelemetry. Same service name as the tracing pipeline, so logs and spans
// correlate under one operation_Id in App Insights. IncludeScopes/ParseStateValues keep the
// structured state searchable rather than flattening to a rendered string.
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    logging.ParseStateValues = true;
    logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("LeaseBook.Web"));
});

// Provider-scoped: quiets EF's per-query Information logs on the EXPORT channel only — the local
// dev console keeps showing SQL exactly as today (ADR-025; appsettings filters only
// Microsoft.AspNetCore, so without this every Executed DbCommand would ship to App Insights).
builder.Logging.AddFilter<OpenTelemetryLoggerProvider>("Microsoft.EntityFrameworkCore", LogLevel.Warning);

var appInsightsConnection = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(appInsightsConnection))
{
    telemetry.WithTracing(tracing => tracing.AddAzureMonitorTraceExporter(
        exporter => exporter.ConnectionString = appInsightsConnection));

    // Logs ride the same connection string; like tracing, this is a no-op locally.
    builder.Logging.AddOpenTelemetry(logging => logging.AddAzureMonitorLogExporter(
        exporter => exporter.ConnectionString = appInsightsConnection));
}

// The trust-invariant sweep body (§C.7), shared by the `check-invariants` CLI verb and the nightly
// job. Registered unconditionally: the verb must work with Jobs:Enabled=false, which is every local
// and CI run. Singleton because it creates one scope per org itself (see the class comment).
builder.Services.AddSingleton<ISweepRunner, InvariantSweepRunner>();

// The lifecycle is the only place process mode affects the service graph (ADR-042). All application
// modules and callable cores above remain shared; only mode-sensitive host infrastructure varies.
lifecycle.Configure(builder);

var app = builder.Build();

var exitCode = await lifecycle.RunAsync(
    app,
    configuredApp =>
    {
        configuredApp.UseExceptionHandler();
        configuredApp.UseMiddleware<LeaseBook.Web.Security.SecurityHeadersMiddleware>();

        if (configuredApp.Environment.IsDevelopment())
        {
            configuredApp.MapOpenApi().AllowAnonymous(); // GET /openapi/v1.json
        }

        // Production serving model (P16): one container serves the API under /api and the built SPA
        // as static files with SPA fallback. In dev these are no-ops (Vite owns the SPA).
        configuredApp.UseDefaultFiles();
        configuredApp.UseStaticFiles();

        configuredApp.UseAuthentication();
        // XSRF precedes authorization/org context so a rejected request opens no transaction.
        configuredApp.UseMiddleware<ApiAntiforgeryMiddleware>();
        configuredApp.UseAuthorization();
        configuredApp.UseRateLimiter();
        // Establish app.org_id inside a per-request transaction for authenticated requests (§C.4).
        configuredApp.UseMiddleware<OrgContextMiddleware>();

        configuredApp.MapModuleEndpoints(endpointAssemblies);
        configuredApp.MapFallbackToFile("index.html").AllowAnonymous();
    },
    CancellationToken.None);

if (exitCode is { } processExitCode)
{
    Environment.ExitCode = processExitCode;
}

// Exposed so the integration test project can drive the host with WebApplicationFactory.
public partial class Program
{
}
