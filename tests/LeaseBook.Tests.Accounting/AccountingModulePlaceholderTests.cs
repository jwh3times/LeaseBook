using LeaseBook.Modules.Accounting;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// Placeholder for the highest-rigor suite in the codebase. M1 fills this with the trust-equation
/// invariant, property-based, and golden-file tests. For M0 it only proves the project is wired
/// into the xUnit v3 pipeline.
/// </summary>
public sealed class AccountingModulePlaceholderTests
{
    [Fact]
    public void Module_assembly_is_referenceable()
    {
        typeof(ModuleMarker).Assembly.GetName().Name.ShouldBe("LeaseBook.Modules.Accounting");
    }
}
