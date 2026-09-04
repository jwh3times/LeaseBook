using System.Reflection;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The compiled LeaseBook surface inspected by architecture tests. Keeping the catalog here means a
/// new module cannot be added to one graph rule while remaining invisible to another.
/// </summary>
internal static class ArchitectureAssemblies
{
    public static Assembly SharedKernel { get; } = typeof(SharedKernel.Cqrs.ISender).Assembly;

    public static Assembly Web { get; } = typeof(Program).Assembly;

    public static Assembly Migrator { get; } = typeof(LeaseBook.Migrator.AppFolioImportCatalog).Assembly;

    public static IReadOnlyList<NamedArchitectureAssembly> Modules { get; } =
    [
        new("LeaseBook.Modules.Accounting", typeof(Modules.Accounting.ModuleMarker).Assembly),
        new("LeaseBook.Modules.Directory", typeof(Modules.Directory.ModuleMarker).Assembly),
        new("LeaseBook.Modules.Banking", typeof(Modules.Banking.ModuleMarker).Assembly),
        new("LeaseBook.Modules.Reporting", typeof(Modules.Reporting.ModuleMarker).Assembly),
        new("LeaseBook.Modules.Operations", typeof(Modules.Operations.ModuleMarker).Assembly),
        new("LeaseBook.Modules.Payments", typeof(Modules.Payments.ModuleMarker).Assembly),
        new("LeaseBook.Modules.Capabilities", typeof(Modules.Capabilities.ModuleMarker).Assembly),
    ];

    public static IReadOnlyList<Assembly> Application { get; } =
    [
        SharedKernel,
        Web,
        Migrator,
        .. Modules.Select(module => module.Assembly),
    ];
}

internal readonly record struct NamedArchitectureAssembly(string Name, Assembly Assembly);
