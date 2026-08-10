using System.Reflection;
using NetArchTest.Rules;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// Enforces the modular-monolith boundary (CLAUDE.md): modules reference SharedKernel only,
/// only the host references modules, SharedKernel references no module. The assembly-level rule
/// is absolute, so it is checked directly via assembly references; NetArchTest adds the
/// namespace-dependency view on top.
/// </summary>
public sealed class ModuleBoundaryTests
{
    private static string[] ReferencedNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToArray();

    [Fact]
    public void Modules_reference_neither_other_modules_nor_the_host()
    {
        var moduleNames = ArchitectureAssemblies.Modules.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var (name, assembly) in ArchitectureAssemblies.Modules)
        {
            var forbidden = ReferencedNames(assembly)
                .Where(r => r == "LeaseBook.Web" || (moduleNames.Contains(r) && r != name))
                .ToArray();

            forbidden.ShouldBeEmpty(
                $"{name} must reference SharedKernel only, but references: {string.Join(", ", forbidden)}");
        }
    }

    [Fact]
    public void SharedKernel_references_no_module_and_not_the_host()
    {
        var refs = ReferencedNames(ArchitectureAssemblies.SharedKernel);

        refs.ShouldNotContain("LeaseBook.Web");
        refs.Where(r => r.StartsWith("LeaseBook.Modules.", StringComparison.Ordinal))
            .ShouldBeEmpty("SharedKernel must not depend on any module");
    }

    [Fact]
    public void Host_references_every_module()
    {
        var refs = ReferencedNames(ArchitectureAssemblies.Web).ToHashSet(StringComparer.Ordinal);

        foreach (var (name, _) in ArchitectureAssemblies.Modules)
        {
            refs.ShouldContain(name, $"the host must reference module {name}");
        }
    }

    [Fact]
    public void Migrator_references_only_sharedkernel_among_leasebook_assemblies()
    {
        var leaseBookRefs = ReferencedNames(ArchitectureAssemblies.Migrator)
            .Where(r => r.StartsWith("LeaseBook.", StringComparison.Ordinal))
            .ToArray();

        leaseBookRefs.ShouldNotContain("LeaseBook.Web");
        leaseBookRefs.Where(r => r.StartsWith("LeaseBook.Modules.", StringComparison.Ordinal))
            .ShouldBeEmpty("Migrator must reference SharedKernel only");
    }

    [Fact]
    public void SharedKernel_types_have_no_dependency_on_any_module_namespace()
    {
        var forbidden = ArchitectureAssemblies.Modules.Select(m => m.Name).Append("LeaseBook.Web").ToArray();

        var result = Types.InAssembly(ArchitectureAssemblies.SharedKernel)
            .Should()
            .NotHaveDependencyOnAny(forbidden)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"SharedKernel types depend on a module/host namespace: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Module_types_have_no_dependency_on_another_module_namespace()
    {
        foreach (var (name, assembly) in ArchitectureAssemblies.Modules)
        {
            var otherModules = ArchitectureAssemblies.Modules
                .Where(m => m.Name != name)
                .Select(m => m.Name)
                .ToArray();

            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(otherModules)
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(
                $"{name} depends on another module namespace: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }
}
