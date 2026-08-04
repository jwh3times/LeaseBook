using LeaseBook.Modules.Operations.Domain;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// Identifies the calendar period of a bulk run. <see cref="Key"/> produces the canonical
/// string form used in <c>source_ref</c> keys (ADR-019).
/// </summary>
public sealed record RunPeriod(int Year, int Month)
{
    /// <summary>Canonical period string for <c>source_ref</c> construction, e.g. <c>"2026-06"</c>.</summary>
    public string Key => $"{Year}-{Month:00}";
}

/// <summary>
/// One row in a <see cref="RunPreview"/> — the operator sees this before choosing to confirm.
/// </summary>
/// <param name="TargetKind">Lease or Owner.</param>
/// <param name="TargetId">Id of the targeted entity.</param>
/// <param name="Label">Human-readable label (tenant name, owner name, etc.).</param>
/// <param name="Amount">Amount that would be posted.</param>
/// <param name="AlreadyDone">True when a source_ref match exists — confirming would produce a Skipped item.</param>
/// <param name="ExcludedReason">Non-null when this target is ineligible and should not be selected.</param>
/// <param name="Detail">Strategy-specific key/value metadata shown in the UI.</param>
public sealed record PreviewRow(
    RunTargetKind TargetKind,
    Guid TargetId,
    string Label,
    decimal Amount,
    bool AlreadyDone,
    string? ExcludedReason,
    IReadOnlyDictionary<string, string> Detail);

/// <summary>
/// The result of <see cref="IRunStrategy.PreviewAsync"/> — the full picture of what a run would do,
/// before the operator commits.
/// </summary>
public sealed record RunPreview(
    RunType RunType,
    RunPeriod Period,
    IReadOnlyList<PreviewRow> Rows,
    IReadOnlyList<string> Exceptions)
{
    /// <summary>
    /// The opaque capability-version token the operator's confirm must echo back (ADR-028). A
    /// strategy never sets this — it is a property of the ENGINE's resolution, not of the row
    /// computation — so <see cref="RunEngine.PreviewAsync"/> stamps it on the way out.
    /// <para>
    /// The default is empty rather than a sentinel that means "skip the check", and that direction
    /// is deliberate: an engine path that forgot to stamp it yields a token that matches nothing, so
    /// the confirm is REJECTED rather than silently unguarded. A wrongly-rejected confirm costs a
    /// re-preview; a silently-unguarded one costs the guarantee this token exists for.
    /// </para>
    /// </summary>
    public string CapabilitiesVersion { get; init; } = string.Empty;
}

/// <summary>
/// The result returned by <see cref="RunEngine.ConfirmAsync"/> — the persisted run's id plus the
/// summary counts and total amount.
/// </summary>
public sealed record RunResult(Guid RunId, int Posted, int Skipped, int Excluded, decimal Total);
