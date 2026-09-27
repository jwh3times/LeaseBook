using LeaseBook.Modules.Payments.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Payments.Features;

public sealed record PaymentView(Guid Id, decimal Amount, string Currency, string Status, bool ReceiptRecorded,
    string? Reason, DateTime CreatedAt, DateTime? LastAttemptAt, bool CanRetry, string? EvidenceReference)
{
    public static PaymentView From(PaymentOperation op, string? evidenceReference = null) => new(op.Id, op.Amount, op.Currency, op.Status,
        op.JournalId is not null, op.Reason, op.CreatedAt, op.LastAttemptAt,
        op.Status == "NeedsReview" && op.Reason == "technical_failure" && op.JournalId is null, evidenceReference);
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
        return rows.Select(x => PaymentView.From(x, x.ProviderId is null ? null : references.GetValueOrDefault(x.ProviderId))).ToArray();
    }
}

public sealed record GetUnmatchedPaymentObservations : IQuery<int>;
internal sealed class GetUnmatchedPaymentObservationsHandler(DbContext db, TimeProvider clock)
    : IQueryHandler<GetUnmatchedPaymentObservations, int>
{
    public Task<int> Handle(GetUnmatchedPaymentObservations q, CancellationToken ct)
    {
        var threshold = clock.GetUtcNow().UtcDateTime.AddMinutes(-10);
        return db.Set<PaymentObservation>().CountAsync(x => x.CreatedAt < threshold
            && !db.Set<PaymentOperation>().Any(o => o.ProviderId == x.ProviderId), ct);
    }
}
