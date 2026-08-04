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
/// <param name="Enabled">Names of the capabilities that resolved to "on" for this unit of work.</param>
/// <param name="Version">
/// The opaque version token of the resolved set. Recorded in <c>bulk_runs.summary_json</c> so a
/// committed run states which capability state it ran under, and used by the preview/confirm
/// concurrency check.
/// </param>
public sealed record RunCapabilities(IReadOnlySet<string> Enabled, string Version)
{
    public bool IsEnabled(string capabilityName) => Enabled.Contains(capabilityName);
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
