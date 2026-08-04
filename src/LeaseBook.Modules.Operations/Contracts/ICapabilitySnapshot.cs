namespace LeaseBook.Modules.Operations.Contracts;

/// <summary>
/// Operations' own view of a frozen capability set (ADR-028). Deliberately NOT a SharedKernel type
/// and NOT the Capabilities module's <c>CapabilitySet</c>: every module depends on SharedKernel and
/// Accounting is a posting path, so a capability type declared there would be reachable from posting
/// code while every reference-graph test in <c>MoneyPathBoundaryTests</c> stayed green — a gate
/// passing on a rule it no longer enforces. The host adapter maps the real set into this view.
/// <para>
/// <b>What this may and may not decide.</b> A capability may gate whether a posting path is
/// REACHABLE — endpoint, command, or run-strategy selection. It may never change the lines or amounts
/// an existing business event produces; money-affecting parameters live in <c>OrgSettings</c>, which
/// is org-scoped, RLS'd, audited, seeded and golden-pinned. Concretely: no value read off this type
/// may become an argument to an Accounting command, business event, or posting-template input.
/// </para>
/// </summary>
/// <param name="Values">
/// The COMPLETE resolved map, one entry per capability in the registry — not the enabled subset. The
/// producing type asserts that completeness and explains why: an absent entry read as "off" is
/// indistinguishable from a deployment-wide kill switch, and effectively undiagnosable in production.
/// Projecting down to enabled-names-only here would throw that guarantee away one hop later, on the
/// money path, which is the worst place to lose it.
/// </param>
/// <param name="Version">
/// The opaque version token of the resolved set. Recorded in <c>bulk_runs.summary_json</c> so a
/// committed run states which capability state it ran under, and used by the preview/confirm
/// concurrency check.
/// </param>
public sealed record RunCapabilities(IReadOnlyDictionary<string, bool> Values, string Version)
{
    /// <summary>
    /// Answers for a resolved capability, and THROWS for anything else rather than answering "off".
    /// <para>
    /// A typo'd, renamed or retired name is a bug in the calling code, not a state. Answering false
    /// would mean a money-path gate silently closes: charges quietly do not post, every downstream
    /// figure is internally consistent, and nothing anywhere records that a gate was consulted with a
    /// name that no longer exists. Throwing turns a silent fiduciary failure into a loud one, which
    /// on this path is strictly the better direction.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capabilityName"/> is not in the resolved set.
    /// </exception>
    public bool IsEnabled(string capabilityName) =>
        Values.TryGetValue(capabilityName, out var enabled)
            ? enabled
            : throw new ArgumentOutOfRangeException(
                nameof(capabilityName), capabilityName,
                "Not a resolved capability. A silent false here would be indistinguishable from a " +
                "kill switch. Capability names come from the registry; a literal that no longer " +
                "resolves is a bug, not an 'off'.");

    /// <summary>Names that resolved to "on", ordered, for recording in a run summary.</summary>
    public IReadOnlyList<string> EnabledNames() =>
        Values.Where(kv => kv.Value).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToArray();
}

/// <summary>
/// Consumer-owned port (ADR-007): Operations must not reference the Capabilities module directly.
/// The host implements this as a thin adapter delegating to <c>ICapabilityGate</c>.
/// <para>
/// <b>The adapter runs on the ambient RLS transaction and must not open a new connection or scope.</b>
/// <c>ICapabilityGate.ResolveDurableAsync</c> already reads on the ambient one; reaching for
/// <c>IPlatformScope</c> there would call <c>BeginTransactionAsync</c> with a transaction already
/// open and throw.
/// </para>
/// <para>
/// Missing org context faults the returned task rather than throwing synchronously, mirroring the
/// gate. Do not "simplify" the adapter into an expression-bodied pass-through that reintroduces a
/// synchronous throw: the difference is invisible under a bare await and load-bearing the moment a
/// caller composes several of these with <c>Task.WhenAll</c>.
/// </para>
/// </summary>
public interface ICapabilitySnapshot
{
    Task<RunCapabilities> ResolveDurableAsync(CancellationToken ct);
}
