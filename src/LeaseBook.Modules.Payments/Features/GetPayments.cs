using LeaseBook.Modules.Payments.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Payments.Features;

public sealed record PaymentView(Guid Id, decimal Amount, string Currency, string Status, bool ReceiptRecorded,
    string? Reason, DateTime CreatedAt, DateTime? LastAttemptAt, bool CanRetry, string? EvidenceReference,
    bool CanPostReturn = false, bool CanCloseReview = false, Guid? ReceiptEntryId = null, Guid? ReturnEntryId = null,
    string? ReviewNote = null, DateTime? ReviewClosedAt = null,
    // Amount is the ledger amount: what the tenant is credited. The fee rides on top and is never posted.
    string Method = PaymentMethods.Ach, decimal QuotedFee = 0m, decimal ChargedAmount = 0m, DateTime? PaidAt = null)
{
    public static PaymentView From(PaymentOperation op, string? evidenceReference = null) => new(op.Id, op.Amount, op.Currency, op.Status,
        op.JournalId is not null, TenantReason(op.Reason), op.CreatedAt, op.LastAttemptAt,
        op.Status == "NeedsReview" && op.Reason == "technical_failure" && op.JournalId is null, evidenceReference)
    {
        Method = op.Method,
        QuotedFee = op.QuotedFee,
        ChargedAmount = op.ChargedAmount,
        PaidAt = op.PaidAt,
    };

    // Why a return could not be posted describes the owner's and the ledger's position, which is staff
    // knowledge. A tenant is told only that a return is under review.
    private static string? TenantReason(string? reason) =>
        reason == "evidence_after_return" || reason?.StartsWith("return_", StringComparison.Ordinal) == true
            ? "return_requires_review" : reason;

    /// <summary>
    /// The staff view: adds the review actions, the journal references and the closing note. A tenant
    /// gets <see cref="From"/>, which carries none of them — the note is written for staff.
    /// </summary>
    public static PaymentView ForStaff(PaymentOperation op, string? evidenceReference, Guid? returnEntryId) =>
        From(op, evidenceReference) with
        {
            CanPostReturn = op.Status == "NeedsReview" && op.JournalId is not null
                && op.Reason?.StartsWith("return_", StringComparison.Ordinal) == true,
            CanCloseReview = op.Status == "NeedsReview" && op.Reason != "technical_failure",
            Reason = op.Reason,
            ReceiptEntryId = op.JournalId,
            ReturnEntryId = returnEntryId,
            ReviewNote = op.ReviewNote,
            ReviewClosedAt = op.ReviewClosedAt,
        };
}

public sealed record GetPayments(Guid? TenantId, Guid? Id = null) : IQuery<IReadOnlyList<PaymentView>>;
internal sealed class GetPaymentsHandler(DbContext db) : IQueryHandler<GetPayments, IReadOnlyList<PaymentView>>
{
    public async Task<IReadOnlyList<PaymentView>> Handle(GetPayments q, CancellationToken ct)
    {
        var query = db.Set<PaymentOperation>().AsNoTracking();
        if (q.TenantId is { } tenantId) { query = query.Where(x => x.TenantId == tenantId); }
        if (q.Id is { } id) { query = query.Where(x => x.Id == id); }
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(100).ToListAsync(ct);
        var providerIds = rows.Where(x => x.ProviderId is not null).Select(x => x.ProviderId!).ToArray();
        var evidence = await db.Set<PaymentObservation>().AsNoTracking()
            .Where(x => providerIds.Contains(x.ProviderId) && x.EvidenceId != "")
            .Select(x => new { x.ProviderId, x.EvidenceId, x.CreatedAt }).ToListAsync(ct);
        var references = evidence.GroupBy(x => x.ProviderId).ToDictionary(x => x.Key,
            x => x.OrderByDescending(e => e.CreatedAt).First().EvidenceId);
        string? Evidence(PaymentOperation x) => x.ProviderId is null ? null : references.GetValueOrDefault(x.ProviderId);
        if (q.TenantId is not null) { return rows.Select(x => PaymentView.From(x, Evidence(x))).ToArray(); }
        var ids = rows.Select(x => x.Id).ToArray();
        var returned = await db.Set<PaymentEffect>().AsNoTracking()
            .Where(x => ids.Contains(x.OperationId) && x.Kind == "Return")
            .ToDictionaryAsync(x => x.OperationId, x => x.JournalId, ct);
        return rows.Select(x => PaymentView.ForStaff(x, Evidence(x),
            returned.TryGetValue(x.Id, out var entryId) ? entryId : null)).ToArray();
    }
}

/// <summary>
/// The observations no payment answers for: stored more than ten minutes ago with a provider reference
/// no operation carries. One definition, so the staff count and the staff list cannot disagree.
/// </summary>
internal static class UnmatchedPaymentObservations
{
    public const int ListLimit = 100;

    public static IQueryable<PaymentObservation> Query(DbContext db, DateTime now)
    {
        var threshold = now.AddMinutes(-10);
        return db.Set<PaymentObservation>().AsNoTracking().Where(x => x.CreatedAt < threshold
            && !db.Set<PaymentOperation>().Any(o => o.ProviderId == x.ProviderId));
    }
}

public sealed record GetUnmatchedPaymentObservations : IQuery<int>;
internal sealed class GetUnmatchedPaymentObservationsHandler(DbContext db, TimeProvider clock)
    : IQueryHandler<GetUnmatchedPaymentObservations, int>
{
    public Task<int> Handle(GetUnmatchedPaymentObservations q, CancellationToken ct) =>
        UnmatchedPaymentObservations.Query(db, clock.GetUtcNow().UtcDateTime).CountAsync(ct);
}

/// <summary>
/// One unmatched observation as staff see it (#491). Deliberately narrow: no raw payload, signature,
/// account, bank, bank evidence or payout identifier leaves the inbox through this view.
/// </summary>
public sealed record UnmatchedObservationView(Guid Id, DateTime ReceivedAt, string Kind, decimal Amount,
    string Currency, string ProviderReference, int AgeMinutes);

/// <summary>The newest unmatched observations, at most <see cref="UnmatchedPaymentObservations.ListLimit"/>.</summary>
public sealed record GetUnmatchedPaymentObservationList : IQuery<IReadOnlyList<UnmatchedObservationView>>;
internal sealed class GetUnmatchedPaymentObservationListHandler(DbContext db, TimeProvider clock)
    : IQueryHandler<GetUnmatchedPaymentObservationList, IReadOnlyList<UnmatchedObservationView>>
{
    public async Task<IReadOnlyList<UnmatchedObservationView>> Handle(GetUnmatchedPaymentObservationList q, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = await UnmatchedPaymentObservations.Query(db, now)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(UnmatchedPaymentObservations.ListLimit)
            .Select(x => new { x.Id, x.CreatedAt, x.Kind, x.Gross, x.Currency, x.ProviderId }).ToListAsync(ct);
        return rows.Select(x => new UnmatchedObservationView(x.Id, x.CreatedAt, x.Kind, x.Gross, x.Currency,
            x.ProviderId, (int)(now - x.CreatedAt).TotalMinutes)).ToArray();
    }
}
