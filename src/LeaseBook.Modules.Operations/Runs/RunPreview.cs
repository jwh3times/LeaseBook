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
    /// The default is empty rather than a sentinel meaning "skip the check", and that direction is
    /// deliberate: an engine path that forgot to stamp it yields a token that matches nothing, so a
    /// confirm carrying it is REJECTED rather than silently unguarded.
    /// </para>
    /// <para>
    /// <b>What that rejection actually is, in each of the two paths.</b> Over HTTP an unstamped token
    /// never reaches the engine: <c>POST /runs/{type}/confirm</c> rejects a blank
    /// <c>capabilitiesVersion</c> with <b>400 <c>capabilities_version_required</c></b>. That is a
    /// client-contract violation, not a state change, and the SPA does not recover from it — it
    /// branches on <c>capabilities_changed</c> only, so it will not re-preview and the operator is
    /// shown a message that reloading cannot clear. In process, a caller passing the empty string
    /// reaches the version comparison and gets <c>CapabilitiesChangedException</c>, because empty
    /// equals no resolved version; passing <c>null</c> is the documented way to say "there is no
    /// preview to honour" and skips the comparison entirely.
    /// </para>
    /// <para>
    /// Both are the safe direction — nothing posts — but neither is a cheap re-preview, so a forgotten
    /// stamp is a bug to fix in the engine, not a cost to absorb at the keyboard.
    /// </para>
    /// </summary>
    public string CapabilitiesVersion { get; init; } = string.Empty;
}

/// <summary>
/// The result returned by <see cref="RunEngine.ConfirmAsync"/> — the persisted run's id plus the
/// summary counts and total amount.
/// </summary>
public sealed record RunResult(Guid RunId, int Posted, int Skipped, int Excluded, decimal Total);
