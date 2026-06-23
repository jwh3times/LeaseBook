using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Operations.Domain;

/// <summary>
/// One per-target outcome within a <see cref="BulkRun"/>. Write-once (append-only): created at
/// commit time and never mutated. <c>snapshot_json</c> is stored as <c>jsonb</c> and carries any
/// per-item metadata the strategy wants to record (amounts, dimension ids, etc.).
/// </summary>
public sealed class BulkRunItem : IOrgScoped
{
    private BulkRunItem()
    {
        // EF parameterless constructor + the factory below.
        SnapshotJson = null!;
    }

    private BulkRunItem(
        Guid runId,
        RunTargetKind targetKind,
        Guid targetId,
        RunItemStatus status,
        decimal amount,
        string? snapshotJson,
        DateTime createdAt)
    {
        Id = UuidV7.NewId();
        RunId = runId;
        TargetKind = targetKind;
        TargetId = targetId;
        Status = status;
        Amount = amount;
        SnapshotJson = snapshotJson ?? "{}";
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    /// <inheritdoc />
    public Guid OrgId { get; set; }

    /// <summary>The parent run's id (FK → <c>bulk_runs.id</c>).</summary>
    public Guid RunId { get; private set; }

    public RunTargetKind TargetKind { get; private set; }

    /// <summary>The id of the targeted entity (lease id or owner id).</summary>
    public Guid TargetId { get; private set; }

    public RunItemStatus Status { get; private set; }

    /// <summary>The amount posted (or 0 for skipped/excluded items).</summary>
    public decimal Amount { get; private set; }

    /// <summary>
    /// Per-item metadata snapshot (jsonb). Strategies may include amounts, journal entry ids, or
    /// source_ref values. Written once; never deserialized by the engine.
    /// </summary>
    public string SnapshotJson { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>
    /// Internal factory — the <see cref="IRunStrategy"/> implementations are the callers.
    /// <paramref name="snapshotJson"/> may be null; it defaults to <c>{}</c>.
    /// <paramref name="createdAt"/> must be supplied by the caller (from the engine's injected
    /// <see cref="TimeProvider"/>) so that test clocks control item timestamps consistently with
    /// the parent <see cref="BulkRun"/>.
    /// </summary>
    internal static BulkRunItem Create(
        Guid runId,
        RunTargetKind targetKind,
        Guid targetId,
        RunItemStatus status,
        decimal amount,
        string? snapshotJson,
        DateTime createdAt) =>
        new(runId, targetKind, targetId, status, amount, snapshotJson, createdAt);
}
