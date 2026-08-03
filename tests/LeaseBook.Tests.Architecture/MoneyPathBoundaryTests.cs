using System.Reflection;
using NetArchTest.Rules;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// A capability may gate whether a posting path is REACHABLE. It may never change the lines or
/// amounts an existing business event produces (ADR-028). Money-affecting PARAMETERS live in
/// OrgSettings — org-scoped, RLS'd, automatically audited through IOrgScoped, seeded, and
/// golden-pinned. Capabilities are none of those things.
/// <para>
/// Concretely: a capability value must never be an input to a posting-template amount computation.
/// The enforceable proxy is that Accounting cannot reference the capability seam at all — gating
/// happens at the caller (endpoint, command, run-strategy selection), never inside posting.
/// </para>
/// <para>
/// This is a dedicated, named gate rather than folded into <see cref="ModuleBoundaryTests"/> on
/// purpose. That suite's generic loop over its hardcoded module array already catches this
/// incidentally today, but only as a side effect of Capabilities being listed there — drop a module
/// from that array (or add a narrower exemption to it for an unrelated reason) and this specific,
/// fiduciary-load-bearing rule goes unchecked with no test name pointing at it. This file pins the
/// rule on its own, with its own rationale, independent of that array's shape.
/// </para>
/// <para>
/// <see cref="Contracts.CapabilitySet"/> deliberately does not live in SharedKernel — every module
/// depends on SharedKernel, so putting it there would let Accounting reach a capability value while
/// this test (and the generic module-boundary loop) stayed green, because SharedKernel references are
/// exempt from the module-isolation rule everywhere. Keeping it a Capabilities-module-owned type is
/// what makes "Accounting does not reference LeaseBook.Modules.Capabilities" an accurate proxy for
/// "Accounting cannot read a capability value" — check <see cref="ModuleBoundaryTests"/>'s
/// SharedKernel assertions if that ever changes.
/// </para>
/// </summary>
public sealed class MoneyPathBoundaryTests
{
    private static readonly Assembly Accounting = typeof(Modules.Accounting.ModuleMarker).Assembly;

    [Fact]
    public void Accounting_does_not_reference_the_capability_module()
    {
        var referenced = Accounting.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n == "LeaseBook.Modules.Capabilities")
            .ToArray();

        referenced.ShouldBeEmpty(
            "Accounting must not reference the capability seam — a capability may gate whether a " +
            "posting path is reachable, never what it posts. Move the gate to the caller.");
    }

    [Fact]
    public void No_accounting_type_depends_on_the_capability_namespace()
    {
        var result = Types.InAssembly(Accounting)
            .That().ResideInNamespace("LeaseBook.Modules.Accounting")
            .ShouldNot().HaveDependencyOn("LeaseBook.Modules.Capabilities")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"offending types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
