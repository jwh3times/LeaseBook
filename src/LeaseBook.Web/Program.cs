using System.Reflection;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Module assemblies the host composes. CQRS handlers/validators and endpoint modules are
// discovered from these. (EF, tenancy, identity, and real slices are wired in later WPs.)
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

var app = builder.Build();

app.MapModuleEndpoints(moduleAssemblies);
app.MapGet("/", () => "LeaseBook host. Health endpoint (/api/health) arrives in WP-06/§C.7.");

app.Run();

// Exposed so the integration test project can drive the host with WebApplicationFactory (later WPs).
public partial class Program
{
}
