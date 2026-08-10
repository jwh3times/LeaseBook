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
/// purpose. That suite's generic loop over its hardcoded module array already catches the plain
/// cross-module-reference case incidentally today, but only as a side effect of Capabilities being
/// listed there — drop a module from that array (or add a narrower exemption to it for an unrelated
/// reason) and this specific, fiduciary-load-bearing rule goes unchecked with no test name pointing
/// at it. This file pins the rule on its own, with its own rationale, independent of that array's
/// shape.
/// </para>
/// <para>
/// <c>LeaseBook.Modules.Capabilities.Contracts.CapabilitySet</c> deliberately does not live in
/// SharedKernel — every module depends on SharedKernel, so putting a capability type there (or
/// declaring a slim lookup interface there because it "feels shared") would let Accounting reach a
/// capability value while every reference-graph test in this suite, including
/// <see cref="ModuleBoundaryTests"/>, stayed green, because SharedKernel references are exempt from
/// the module-isolation rule everywhere. Nothing in <see cref="ModuleBoundaryTests"/> checks what
/// SharedKernel <i>declares</i> — its SharedKernel assertions only check the outbound direction
/// (SharedKernel must not depend on a module). <see cref="SharedKernel_declares_no_capability_type"/>
/// below is what actually closes that gap; it is the one assertion in this suite that a move like
/// that cannot pass. <see cref="Accounting_declares_no_locally_cloned_capability_type"/> closes the
/// sibling route: a <c>CapabilitySet</c>-shaped record declared directly inside Accounting and
/// populated by the host, which no reference to <c>LeaseBook.Modules.Capabilities</c> would ever
/// reveal.
/// </para>
/// </summary>
public sealed class MoneyPathBoundaryTests
{
    private static readonly Assembly Accounting = typeof(Modules.Accounting.ModuleMarker).Assembly;
    private static readonly Assembly SharedKernel = typeof(SharedKernel.Cqrs.ISender).Assembly;
    private static readonly Assembly Capabilities = typeof(Modules.Capabilities.ModuleMarker).Assembly;
    private static readonly string CapabilitiesAssemblyName =
        Capabilities.GetName().Name
        ?? throw new InvalidOperationException("LeaseBook.Modules.Capabilities assembly has no name.");
    private static readonly string CapabilitiesNamespace =
        typeof(Modules.Capabilities.ModuleMarker).Namespace
        ?? throw new InvalidOperationException("ModuleMarker has no namespace.");

    private const string Rule =
        "a capability may gate whether a posting path is reachable, never what it posts (ADR-028). " +
        "Move the gate to the caller — endpoint, command, or run-strategy selection.";

    [Fact]
    public void Accounting_does_not_reference_the_capability_module()
    {
        var referenced = Accounting.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n == CapabilitiesAssemblyName)
            .ToArray();

        referenced.ShouldBeEmpty(
            $"Accounting must not reference the capability seam ({CapabilitiesAssemblyName}) — {Rule}");
    }

    [Fact]
    public void No_accounting_type_depends_on_the_capability_namespace()
    {
        // Unfiltered over the whole assembly, deliberately: a .That().ResideInNamespace(...) filter
        // here only narrows which offending types get reported, it cannot make the check stronger,
        // and it risks silently exempting a type in a namespace nobody thought to list.
        var result = Types.InAssembly(Accounting)
            .ShouldNot().HaveDependencyOn(CapabilitiesNamespace)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"no Accounting type may depend on {CapabilitiesNamespace} — {Rule} " +
            $"Offending types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    /// <summary>
    /// The assertion <see cref="ModuleBoundaryTests"/> structurally cannot make. Its SharedKernel
    /// checks are all outbound ("SharedKernel must not depend on a module"); nothing in that suite
    /// checks what SharedKernel itself declares. If a capability type — or a slim lookup interface
    /// that "feels shared" — moved into SharedKernel, every module (Accounting included) would reach
    /// it for free with all 14 tests in this suite green, this one excepted. Name-substring matching,
    /// not a namespace check, on purpose: a capability type dropped into an existing SharedKernel
    /// namespace (e.g. <c>LeaseBook.SharedKernel.Cqrs</c>) would dodge a namespace-anchored check.
    /// </summary>
    [Fact]
    public void SharedKernel_declares_no_capability_type()
    {
        AssertNoCapabilityLikeType(SharedKernel, "SharedKernel");
    }

    /// <summary>
    /// Closes the sibling route to the one above: a <c>CapabilitySet</c>-shaped type declared
    /// directly inside Accounting (never referencing <c>LeaseBook.Modules.Capabilities</c> at all)
    /// and populated by the host adapter. Every reference-graph assertion above is green against
    /// that shape, because it never names the Capabilities assembly or namespace.
    /// </summary>
    [Fact]
    public void Accounting_declares_no_locally_cloned_capability_type()
    {
        AssertNoCapabilityLikeType(Accounting, "Accounting");
    }

    private static void AssertNoCapabilityLikeType(Assembly assembly, string assemblyLabel)
    {
        var offenders = assembly.GetTypes()
            .Where(t => t.Name.Contains("Capabilit", StringComparison.OrdinalIgnoreCase)
                     || t.Name.Contains("Entitlement", StringComparison.OrdinalIgnoreCase)
                     || t.Name.Contains("FeatureFlag", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName!)
            .ToArray();

        offenders.ShouldBeEmpty(
            $"{assemblyLabel} must declare no capability-shaped type. Every module depends on " +
            "SharedKernel, and Accounting is a posting path, so a capability type declared in either " +
            "place is reachable from posting code with every other reference-graph test in this " +
            "suite green. Capability types live in LeaseBook.Modules.Capabilities.Contracts; " +
            "per-module views (e.g. Operations' RunCapabilities) are declared by the consuming " +
            $"module, never by Accounting or SharedKernel. Offending types: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// Reflection and NetArchTest both see only type-level links. A reflection-hiding route —
    /// <c>Type.GetType("...CapabilitySet, LeaseBook.Modules.Capabilities")</c>, or a raw
    /// <c>feature_flags</c> string-keyed lookup that never names a Capabilities type — is invisible
    /// to <see cref="Accounting_does_not_reference_the_capability_module"/> and
    /// <see cref="No_accounting_type_depends_on_the_capability_namespace"/> alike. The compiled-code
    /// module closes that gap through IL member references and string literals, without pinning the
    /// gate to source paths, comments, or formatting.
    /// </summary>
    [Fact]
    public void No_accounting_compiled_code_references_the_capability_seam()
    {
        var offenders = CapabilityCodeGuard.FindMentions(CompiledCode.In(Accounting))
            .Select(reference => reference.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            $"Accounting compiled code references the capability seam by name — {Rule} " +
            "This catches what reflection cannot: Type.GetType(\"...CapabilitySet, " +
            "LeaseBook.Modules.Capabilities\") and raw feature_flags/IsEnabled lookups are invisible " +
            "to the assembly- and namespace-dependency checks above." +
            (offenders.Length == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, offenders)));
    }
}
