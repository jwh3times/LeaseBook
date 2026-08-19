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
/// What a strategy computes for a period: the rows, and any exceptions it hit while computing them.
/// <para>
/// This is deliberately <b>not</b> a <see cref="RunPreview"/>. A preview carries the capability-version
/// token the operator must echo back on confirm, and that token is a property of the ENGINE's
/// resolution — a strategy has no way to know it and no business inventing one. Splitting the two
/// types is what makes an unstamped preview unconstructible rather than merely discouraged.
/// </para>
/// </summary>
public sealed record StrategyPreview(
    IReadOnlyList<PreviewRow> Rows,
    IReadOnlyList<string> Exceptions);

/// <summary>
/// The full picture of what a run would do, before the operator commits — a strategy's
/// <see cref="StrategyPreview"/> plus the run's identity and the engine's capability-version token.
/// <para>
/// <b>Only <see cref="RunEngine.PreviewAsync"/> can build one</b>, because
/// <see cref="CapabilitiesVersion"/> is a required constructor argument and the engine is the only
/// place that resolves it. That is the point of the shape: this record previously defaulted the token
/// to the empty string and relied on the engine remembering to stamp it afterwards through a
/// <c>with</c> expression. The empty default failed closed — a confirm carrying it was rejected rather
/// than silently unguarded — but "safe when someone forgets" is a weaker guarantee than "impossible to
/// forget", and the rejection it produced was not cheap: over HTTP the SPA branches on
/// <c>capabilities_changed</c> only, so a blank token yields a
/// <b>400 <c>capabilities_version_required</c></b> the operator cannot clear by reloading.
/// </para>
/// </summary>
public sealed record RunPreview(
    RunType RunType,
    RunPeriod Period,
    IReadOnlyList<PreviewRow> Rows,
    IReadOnlyList<string> Exceptions,
    string CapabilitiesVersion);

/// <summary>
/// The result returned by <see cref="RunEngine.ConfirmAsync"/> — the persisted run's id plus the
/// summary counts and total amount.
/// </summary>
public sealed record RunResult(Guid RunId, int Posted, int Skipped, int Excluded, decimal Total);
