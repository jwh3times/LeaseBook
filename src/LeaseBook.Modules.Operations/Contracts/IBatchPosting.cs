namespace LeaseBook.Modules.Operations.Contracts;

// ── Intent DTOs ───────────────────────────────────────────────────────────────
// Declared in Contracts so IRunStrategy implementations (same module) and the host adapter both
// share the same types. Modules.Operations must NOT reference Accounting types — the adapter
// (host-side) translates these intents into AccountingEvents.

/// <summary>Intent to post a rent charge for one lease.</summary>
/// <param name="SourceRef">ADR-019 idempotency key: <c>"rent:{period}:lease={leaseId}"</c>.</param>
public sealed record RentChargeIntent(
    Guid LeaseId,
    Guid TenantId,
    Guid PropertyId,
    Guid OwnerId,
    Guid? UnitId,
    decimal Amount,
    DateOnly Date,
    string Description,
    string SourceRef);

/// <summary>Intent to post a late-fee charge for one lease.</summary>
/// <param name="SourceRef">ADR-019 idempotency key: <c>"latefee:{period}:lease={leaseId}"</c>.</param>
public sealed record LateFeeIntent(
    Guid LeaseId,
    Guid TenantId,
    Guid PropertyId,
    Guid OwnerId,
    Guid? UnitId,
    decimal Amount,
    DateOnly Date,
    string Description,
    string SourceRef);

/// <summary>Intent to post a management-fee assessment and an owner disbursement for one owner.</summary>
/// <param name="FeeSourceRef">ADR-019 key: <c>"disbursement-fee:{period}:owner={ownerId}"</c>.</param>
/// <param name="DisburseSourceRef">ADR-019 key: <c>"disbursement:{period}:owner={ownerId}"</c>.</param>
public sealed record DisbursementIntent(
    Guid OwnerId,
    Guid? PropertyId,
    decimal MgmtFee,
    decimal DisburseAmount,
    decimal Reserve,
    DateOnly Date,
    Guid OperatingBankId,
    string Description,
    string FeeSourceRef,
    string DisburseSourceRef);

/// <summary>The two journal-entry ids produced by posting one <see cref="DisbursementIntent"/>.</summary>
/// <param name="FeeEntryId">null when <c>MgmtFee == 0</c>.</param>
public sealed record DisbursementPostingResult(Guid? FeeEntryId, Guid DisbursementEntryId);

// ── Port ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Write-direction cross-module port (ADR-007 / ADR-019). Operations declares the interface;
/// the host adapter (<c>BatchPostingAdapter</c>) implements it, translating intent DTOs into
/// <see cref="LeaseBook.Modules.Accounting.Contracts.IAccountingEvents.PostAsync"/> calls.
/// <para>
/// Each method returns a map of <c>targetId → journalEntryId</c> so the calling strategy can
/// include entry ids in its per-item <c>snapshot_json</c>.
/// The <c>SourceRef</c> on each intent threads through to the accounting layer so the existing
/// <c>(org_id, source_ref)</c> partial unique index provides idempotency without any additional
/// index in the Operations module.
/// </para>
/// </summary>
public interface IBatchPosting
{
    /// <summary>Posts <see cref="RentChargeIntent"/>s; returns <c>leaseId → entryId</c>.</summary>
    Task<IReadOnlyDictionary<Guid, Guid>> PostRentChargesAsync(
        IReadOnlyList<RentChargeIntent> intents, CancellationToken ct);

    /// <summary>Posts <see cref="LateFeeIntent"/>s; returns <c>leaseId → entryId</c>.</summary>
    Task<IReadOnlyDictionary<Guid, Guid>> PostLateFeesAsync(
        IReadOnlyList<LateFeeIntent> intents, CancellationToken ct);

    /// <summary>
    /// Posts a management-fee assessment (when <c>MgmtFee &gt; 0</c>) followed by an owner
    /// disbursement for each <see cref="DisbursementIntent"/>; returns <c>ownerId → result</c>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DisbursementPostingResult>> PostDisbursementsAsync(
        IReadOnlyList<DisbursementIntent> intents, CancellationToken ct);
}
