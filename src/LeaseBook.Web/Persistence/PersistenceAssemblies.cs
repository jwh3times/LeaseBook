using System.Reflection;

namespace LeaseBook.Web.Persistence;

/// <summary>
/// Assemblies whose <c>IEntityTypeConfiguration</c> implementations the single AppDbContext
/// discovers (ADR-004): the host plus every module. Modules contribute mappings here as they grow.
/// </summary>
public static class PersistenceAssemblies
{
    public static readonly Assembly[] ModelAssemblies =
    [
        typeof(AppDbContext).Assembly,
        typeof(Modules.Accounting.ModuleMarker).Assembly,
        typeof(Modules.Directory.ModuleMarker).Assembly,
        typeof(Modules.Banking.ModuleMarker).Assembly,
        typeof(Modules.Reporting.ModuleMarker).Assembly,
        typeof(Modules.Operations.ModuleMarker).Assembly,
        typeof(Modules.Payments.ModuleMarker).Assembly,
    ];
}
