using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Operations.Domain;

/// <summary>
/// The header of a committed bulk run (M6 run-engine). One row per operator-initiated run; the
/// per-target outcomes are <see cref="BulkRunItem"/> rows that FK to this row.
/// <para>
/// Write-once (append-only): once created the aggregate is never updated or deleted. Corrections are
/// not possible — a duplicate source_ref post simply returns <c>Skipped</c> (the idempotency index
/// on <c>journal_entries</c> handles it). <c>summary_json</c> is written once at creation and holds
/// the <see cref="RunResult"/> snapshot; it is stored as <c>jsonb</c> in Postgres.
/// </para>
/// </summary>
public sealed class BulkRun : IOrgScoped
{
    private BulkRun()
    {
        // EF parameterless constructor + the factory below.
        SummaryJson = null!;
    }

    private BulkRun(RunType runType, int periodYear, int periodMonth, string summaryJson, DateTime createdAt)
    {
        Id = UuidV7.NewId();
        RunType = runType;
        PeriodYear = periodYear;
        PeriodMonth = periodMonth;
        SummaryJson = summaryJson;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    /// <inheritdoc />
    public Guid OrgId { get; set; }

    public RunType RunType { get; private set; }

    /// <summary>Calendar year of the run period, e.g. <c>2026</c>.</summary>
    public int PeriodYear { get; private set; }

    /// <summary>Calendar month of the run period (1–12).</summary>
    public int PeriodMonth { get; private set; }

    /// <summary>
    /// JSON snapshot of the <see cref="RunResult"/> (posted/skipped/excluded counts, total amount).
    /// Written once at creation; stored as <c>jsonb</c>. Not deserialized by the engine — the result
    /// is returned directly from <see cref="RunEngine.ConfirmAsync"/>.
    /// </summary>
    public string SummaryJson { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>
    /// Internal factory — only <see cref="RunEngine"/> is the caller. Returns a fresh, unseeded
    /// aggregate whose <c>OrgId</c> is stamped by <c>AppDbContext.SaveChanges</c>.
    /// </summary>
    internal static BulkRun Create(RunType runType, int periodYear, int periodMonth, string summaryJson, DateTime createdAt) =>
        new(runType, periodYear, periodMonth, summaryJson, createdAt);

    /// <summary>
    /// Updates the summary JSON before the first <c>SaveChangesAsync</c> (called by
    /// <see cref="RunEngine"/> once the item counts are known). Only valid while the row is in the
    /// Added state — calling after save breaks the write-once contract.
    /// </summary>
    internal void SetSummaryJson(string json) => SummaryJson = json;
}
