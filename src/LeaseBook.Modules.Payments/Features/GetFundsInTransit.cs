using LeaseBook.Modules.Payments.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Payments.Features;

/// <summary>A tenant's funds in transit: the ledger amounts, the earliest paid date, and how many payments.</summary>
public sealed record TenantInTransit(Guid TenantId, decimal Amount, DateTime PaidAt, int Payments);

/// <summary>
/// Funds in transit (ADR-053): the ledger amounts of payments the processor has reported as collected
/// that no bank evidence has yet answered for. One row per tenant, for the tenants named or for all.
/// <para>
/// A read over Payments' own records. It is never posted, never part of a balance, and never an input
/// to a posting: the journal learns of this money only when the bank shows it.
/// </para>
/// </summary>
public sealed record GetFundsInTransit(IReadOnlyList<Guid>? TenantIds = null) : IQuery<IReadOnlyList<TenantInTransit>>;

internal sealed class GetFundsInTransitHandler(DbContext db) : IQueryHandler<GetFundsInTransit, IReadOnlyList<TenantInTransit>>
{
    public async Task<IReadOnlyList<TenantInTransit>> Handle(GetFundsInTransit q, CancellationToken ct)
    {
        var query = FundsInTransit.Query(db);
        if (q.TenantIds is { } tenantIds) { query = query.Where(x => tenantIds.Contains(x.TenantId)); }
        return await query.GroupBy(x => x.TenantId)
            .Select(g => new TenantInTransit(g.Key, g.Sum(x => x.Amount), g.Min(x => x.PaidAt!.Value), g.Count()))
            .ToListAsync(ct);
    }
}

/// <summary>One definition of "in transit", so every figure that claims it agrees.</summary>
internal static class FundsInTransit
{
    // Collected (the processor reported success), still waiting for the bank (no receipt), and with
    // nothing recorded against it: a failed, returned, held or closed payment is not on its way anywhere.
    public static IQueryable<PaymentOperation> Query(DbContext db) => db.Set<PaymentOperation>().AsNoTracking()
        .Where(x => x.Status == "Processing" && x.PaidAt != null && x.JournalId == null);
}
