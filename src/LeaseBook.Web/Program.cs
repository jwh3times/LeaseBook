using System.Reflection;
using Azure.Monitor.OpenTelemetry.Exporter;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.SharedKernel.Observability;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Module assemblies the host composes. CQRS handlers/validators and endpoint modules are
// discovered from these. (Identity and real slices are wired in later WPs.)
Assembly[] moduleAssemblies =
[
    typeof(LeaseBook.Modules.Accounting.ModuleMarker).Assembly,
    typeof(LeaseBook.Modules.Directory.ModuleMarker).Assembly,
    typeof(LeaseBook.Modules.Banking.ModuleMarker).Assembly,
    typeof(LeaseBook.Modules.Reporting.ModuleMarker).Assembly,
    typeof(LeaseBook.Modules.Operations.ModuleMarker).Assembly,
    typeof(LeaseBook.Modules.Payments.ModuleMarker).Assembly,
];

builder.Services.AddLeaseBookCqrs(moduleAssemblies);

// Data access (runtime = app role, RLS-subject). Migrations use the migrator connection via the
// design-time factory; the running app never connects as migrator.
builder.Services.AddDbContext<AppDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Default"),
        npgsql => npgsql.SetPostgresVersion(18, 0))
    .UseSnakeCaseNamingConvention());

// Tenancy ergonomics: one request-scoped TenantContext, exposed read-only as ITenantContext (which
// the DbContext query filter reads). DbContext is also resolvable as its base type so the
// scheduler-agnostic OrgScopedExecutor can open the unit-of-work transaction.
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<OrgScopedExecutor>();

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

// Establishes app.org_id inside a per-request transaction for authenticated requests (§C.4). Sits
// after authentication (added in WP-06); with no auth yet it passes anonymous requests through.
app.UseMiddleware<OrgContextMiddleware>();

app.MapModuleEndpoints(moduleAssemblies);
app.MapGet("/", () => "LeaseBook host. Health endpoint (/api/health) arrives in WP-06/§C.7.");

app.Run();

// Exposed so the integration test project can drive the host with WebApplicationFactory (later WPs).
public partial class Program
{
}
