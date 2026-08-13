namespace LeaseBook.Modules.Operations.Contracts;

// ── Intent DTOs ───────────────────────────────────────────────────────────────
// Declared in Contracts so IRunStrategy implementations (same module) and the host adapter both
// share the same types. Modules.Operations must NOT reference Accounting types — the adapter
// (host-side) translates these intents into AccountingEvents.

/// <summary>
/// One thing a run wants posted. The closed set below is what <see cref="IBatchPosting"/> accepts;
/// a run type that needs a new shape adds a case here and a branch in the host adapter.
/// </summary>
public abstract record RunIntent;

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
    string SourceRef) : RunIntent;

/// <summary>Intent to post a late-fee charge for one lease.</summary>
/// <param name="RentObligationEntryId">The specific rent journal entry this fee assesses.</param>
/// <param name="SourceRef">Idempotency key: <c>"latefee:rent-entry={rentEntryId}"</c>.</param>
public sealed record LateFeeIntent(
    Guid LeaseId,
    Guid RentObligationEntryId,
    Guid TenantId,
    Guid PropertyId,
    Guid OwnerId,
    Guid? UnitId,
    decimal Amount,
    DateOnly Date,
    string Description,
    string SourceRef) : RunIntent;

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
    string DisburseSourceRef) : RunIntent;

// ── Outcome ───────────────────────────────────────────────────────────────────

/// <summary>
/// Why a single posting attempt ended the way it did. Every value except
/// <see cref="Posted"/> is a per-item refusal the run records and carries on from — none of them
/// aborts the run.
/// </summary>
public enum PostStatus
{
    /// <summary>The entry was written.</summary>
    Posted,

    /// <summary>An entry with this source ref already exists — the run is being repeated (→ Skipped).</summary>
    DuplicateSourceRef,

    /// <summary>The bank period is locked by a finalized reconciliation (→ Excluded).</summary>
    PeriodLocked,

    /// <summary>The accounting period is closed (→ Excluded).</summary>
    PeriodClosed,

    /// <summary>The disbursement would breach the owner's reserve floor (→ Excluded).</summary>
    ReserveFloor,
}

/// <summary>
/// The result of posting one <see cref="RunIntent"/>.
/// <para>
/// <b>Refusals are returned, not thrown.</b> Before this existed, the port's only failure channel
/// was an exception, and because an exception aborts a whole batch every caller had to invoke the
/// batch methods one intent at a time to catch it — the batching was nominal. Worse, Operations
/// cannot reference Accounting, so each strategy classified failures by comparing
/// <c>ex.GetType().Name</c> against string literals: renaming an exception class in Accounting would
/// have silently reclassified a money-path failure as unhandled, with nothing failing anywhere.
/// The host adapter is the one place that can see both assemblies, so it is the one place that
/// should be doing this translation.
/// </para>
/// </summary>
/// <param name="EntryId">The journal entry written, or <see langword="null"/> for any refusal.</param>
/// <param name="FeeEntryId">
/// The management-fee entry, for a disbursement that assessed one. Null for every other intent and
/// for a disbursement whose fee was zero.
/// </param>
public sealed record PostOutcome(PostStatus Status, Guid? EntryId = null, Guid? FeeEntryId = null)
{
    public static PostOutcome Posted(Guid entryId, Guid? feeEntryId = null) =>
        new(PostStatus.Posted, entryId, feeEntryId);

    public static PostOutcome Refused(PostStatus status) => new(status);
}

// ── Port ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Write-direction cross-module port (ADR-007 / ADR-019). Operations declares the interface and owns
/// both the intent shapes and the outcome vocabulary; the host adapter (<c>BatchPostingAdapter</c>)
/// implements it, translating intents into <c>IAccountingEvents.PostAsync</c> calls and Accounting's
/// exceptions into a <see cref="PostOutcome"/>.
/// <para>
/// The <c>SourceRef</c> carried on each intent threads through to the <c>journal_entries</c> row, so
/// the existing <c>(org_id, source_ref)</c> partial unique index provides idempotency without any
/// additional index in the Operations module (ADR-019).
/// </para>
/// </summary>
public interface IBatchPosting
{
    /// <summary>
    /// Posts one intent. Returns how it went; raises only for failures that are genuinely not
    /// per-item — a lost connection, a rolled-back transaction — which the run engine does not catch.
    /// </summary>
    Task<PostOutcome> PostAsync(RunIntent intent, CancellationToken ct);
}
