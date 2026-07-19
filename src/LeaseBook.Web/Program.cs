using System.Reflection;
using System.Threading.RateLimiting;
using Azure.Monitor.OpenTelemetry.Exporter;
using LeaseBook.Modules.Accounting;
using LeaseBook.Modules.Banking;
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
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Reporting;
using LeaseBook.Web.Security;
using LeaseBook.Web.Seeding;
using LeaseBook.Web.Tenancy;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using QuestPDF.Infrastructure;

// QuestPDF Community license (M5 WP-04). Free for organizations under the $1M annual revenue
// threshold; LeaseBook qualifies at launch. Must be set before the first document is rendered.
QuestPDF.Settings.License = LicenseType.Community;

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

// Data access (runtime = app role, RLS-subject). Migrations use the migrator connection via the
// design-time factory; the running app never connects as migrator.
builder.Services.AddDbContext<AppDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Default"),
        npgsql => npgsql.SetPostgresVersion(18, 0))
    .UseSnakeCaseNamingConvention());

// Identity, cookie auth, antiforgery, deny-by-default authorization (P12).
builder.Services.AddLeaseBookIdentity(builder.Environment);

// F6: keyring for the Identity token-store encryption converter (EncryptedStringConverter). Dev/test
// use the default persisted keyring; at go-live the keys move to Key Vault (infra follow-up).
builder.Services.AddDataProtection();

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

// Tenancy ergonomics: one request-scoped TenantContext, exposed read-only as ITenantContext (which
// the DbContext query filter reads). DbContext is also resolvable as its base type so the
// scheduler-agnostic OrgScopedExecutor can open the unit-of-work transaction.
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
// Actor context (P52): the auth middleware populates it from the user-id claim; PostingService and the
// AppDbContext audit pass read it to stamp created_by / actor_user_id. Null for seeder/job writes.
builder.Services.AddScoped<ActorContext>();
builder.Services.AddScoped<IActorContext>(sp => sp.GetRequiredService<ActorContext>());
builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<OrgScopedExecutor>();

// Accounting module services (chart-of-accounts provisioning, period lifecycle; the posting engine
// and event catalog register here in later WPs). They consume the ambient DbContext + ITenantContext.
builder.Services.AddAccountingModule();

// Directory module services (settings/bank/fee config; CQRS handlers are auto-discovered). The host
// implements Directory's cross-module ports with thin adapters (ADR-007 / P49): IChartProvisioner
// delegates bank-account provisioning to the Accounting chart-of-accounts.
builder.Services.AddDirectoryModule();
builder.Services.AddScoped<LeaseBook.Modules.Directory.Contracts.IChartProvisioner, ChartProvisionerAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Directory.Contracts.ITenantFinancials, TenantFinancialsAdapter>();
builder.Services.AddScoped<LeaseBook.Modules.Directory.Contracts.IOwnerFinancials, OwnerFinancialsAdapter>();
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
// / StatementView). Both are scoped — DeliveryRecord insert needs the ambient tenant context.
builder.Services.AddScoped<LeaseBook.Modules.Reporting.Delivery.IArtifactStore,
    LeaseBook.Modules.Reporting.Delivery.LocalArtifactStore>();
builder.Services.AddScoped<IStatementDelivery, LocalStatementDelivery>();

// Operations module services (run engine; CQRS handlers are auto-discovered). The host implements
// Operations' cross-module ports (ADR-007 / ADR-019):
//   IBatchPosting — write-direction: translates run intents into IAccountingEvents.PostAsync calls.
//   ILeaseScheduleData — read-direction: dispatches Directory's GetActiveLeaseSchedule via ISender.
//   IPostedSourceRefs — read-direction: dispatches Accounting's GetExistingSourceRefs via ISender.
builder.Services.AddOperationsModule();
builder.Services.AddScoped<LeaseBook.Modules.Operations.Contracts.IBatchPosting, BatchPostingAdapter>();
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

var appInsightsConnection = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(appInsightsConnection))
{
    telemetry.WithTracing(tracing => tracing.AddAzureMonitorTraceExporter(
        exporter => exporter.ConnectionString = appInsightsConnection));
}

var app = builder.Build();

app.UseExceptionHandler();

app.UseMiddleware<LeaseBook.Web.Security.SecurityHeadersMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous(); // GET /openapi/v1.json
}

// Production serving model (P16): one container serves the API under /api and the built SPA as
// static files with SPA fallback. In dev these are no-ops (Vite serves the SPA; wwwroot is empty).
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
// XSRF on unsafe /api requests, before authorization/org-context so a rejected request opens no tx.
app.UseMiddleware<ApiAntiforgeryMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();
// Establishes app.org_id inside a per-request transaction for authenticated requests (§C.4).
app.UseMiddleware<OrgContextMiddleware>();

app.MapModuleEndpoints(endpointAssemblies);

// SPA fallback: client-side routes resolve to index.html (served from wwwroot in the container).
app.MapFallbackToFile("index.html").AllowAnonymous();

// The four fixed roles must exist before sign-in/seeding (idempotent). Skipped during build-time
// OpenAPI generation (ADR-012): the GetDocument tool runs this program up to app.Run() solely to
// capture the API surface, with no database available — and this is the only pre-Run database call.
// The flag is set only by the CI schema-drift job; it is unset in every real run (dev, prod, tests).
if (Environment.GetEnvironmentVariable("LEASEBOOK_OPENAPI_BUILD") != "1")
{
    await RoleSeeder.EnsureRolesAsync(app.Services);
}

// CLI: `dotnet run --project src/LeaseBook.Web -- seed --org demo` provisions the demo org and exits.
//      `dotnet run --project src/LeaseBook.Web -- seed --org cutover` provisions the cutover org (M7).
if (args is ["seed", ..])
{
    var orgFlag = Array.IndexOf(args, "--org");
    var orgValue = orgFlag >= 0 && orgFlag + 1 < args.Length ? args[orgFlag + 1] : "demo";

    if (string.Equals(orgValue, "cutover", StringComparison.OrdinalIgnoreCase))
    {
        await CutoverSeeder.SeedAsync(app.Services);
    }
    else
    {
        await DemoSeeder.SeedAsync(app.Services);
    }
    return;
}

// CLI: `dotnet run --project src/LeaseBook.Web -- check-invariants [--org <id|demo>|--all]` sweeps the
// core correctness invariants and exits non-zero on any violation (P33).
if (args is ["check-invariants", ..])
{
    Environment.ExitCode = await InvariantSweep.RunAsync(app.Services, args);
    return;
}

// Task 10 (F3a, F8): fail-fast in any non-Development environment if AllowedHosts is left open or
// the Data Protection keyring hasn't been attested durable — a no-op in Development, and skipped
// for the OpenAPI build (no real config/environment is being started up there, same as RoleSeeder
// above) and for the CLI paths (which have already returned above and never reach this line).
if (Environment.GetEnvironmentVariable("LEASEBOOK_OPENAPI_BUILD") != "1")
{
    ProductionSecurityGuards.Validate(app.Configuration, app.Environment);
}

app.Run();

// Exposed so the integration test project can drive the host with WebApplicationFactory.
public partial class Program
{
}
