using System.Reflection;
using Azure.Monitor.OpenTelemetry.Exporter;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.SharedKernel.Observability;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Endpoints;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Seeding;
using LeaseBook.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Module assemblies the host composes. CQRS handlers/validators are discovered from these; endpoint
// modules are discovered from these plus the host (which owns auth/meta/diagnostics endpoints).
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

// Data access (runtime = app role, RLS-subject). Migrations use the migrator connection via the
// design-time factory; the running app never connects as migrator.
builder.Services.AddDbContext<AppDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Default"),
        npgsql => npgsql.SetPostgresVersion(18, 0))
    .UseSnakeCaseNamingConvention());

// Identity, cookie auth, antiforgery, deny-by-default authorization (P12).
builder.Services.AddLeaseBookIdentity();

// Tenancy ergonomics: one request-scoped TenantContext, exposed read-only as ITenantContext (which
// the DbContext query filter reads). DbContext is also resolvable as its base type so the
// scheduler-agnostic OrgScopedExecutor can open the unit-of-work transaction.
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<OrgScopedExecutor>();

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
// Establishes app.org_id inside a per-request transaction for authenticated requests (§C.4).
app.UseMiddleware<OrgContextMiddleware>();

app.MapModuleEndpoints(endpointAssemblies);

// SPA fallback: client-side routes resolve to index.html (served from wwwroot in the container).
app.MapFallbackToFile("index.html").AllowAnonymous();

// The four fixed roles must exist before sign-in/seeding (idempotent).
await RoleSeeder.EnsureRolesAsync(app.Services);

// CLI: `dotnet run --project src/LeaseBook.Web -- seed --org demo` provisions the demo org and exits.
if (args is ["seed", ..])
{
    await DemoSeeder.SeedAsync(app.Services);
    return;
}

app.Run();

// Exposed so the integration test project can drive the host with WebApplicationFactory.
public partial class Program
{
}
