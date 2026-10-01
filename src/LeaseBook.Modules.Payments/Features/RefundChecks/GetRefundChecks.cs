using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Payments.Features.RefundChecks;

/// <summary>Refund checks, newest first, filtered by bank account and/or tenant (#473).</summary>
public sealed record GetRefundChecks(Guid? BankAccountId = null, Guid? TenantId = null, Guid? Id = null)
    : IQuery<IReadOnlyList<RefundCheckView>>;

internal sealed class GetRefundChecksHandler(DbContext db, IRefundCheckLedger ledger)
    : IQueryHandler<GetRefundChecks, IReadOnlyList<RefundCheckView>>
{
    public async Task<IReadOnlyList<RefundCheckView>> Handle(GetRefundChecks q, CancellationToken ct)
    {
        var query = db.Set<RefundCheck>().AsNoTracking();
        if (q.BankAccountId is { } bank) { query = query.Where(x => x.BankAccountId == bank); }
        if (q.TenantId is { } tenant) { query = query.Where(x => x.TenantId == tenant); }
        if (q.Id is { } id) { query = query.Where(x => x.Id == id); }
        var checks = await query.OrderByDescending(x => x.IssueDate).ThenByDescending(x => x.CheckNumber)
            .Take(200).ToListAsync(ct);
        return await RefundCheckReads.ViewsAsync(db, ledger, checks, ct);
    }
}

public sealed record RefundCheckFund(
    string Source, Guid BankAccountId, Guid? PropertyId, Guid? OwnerId, decimal Held, int? NextCheckNumber);

public sealed record RefundCheckOptions(IReadOnlyList<RefundCheckFund> Funds);

/// <summary>
/// What a refund check for this tenant can draw on: each held bucket with the next unused number on its
/// bank (the highest issued or voided refund check plus one; null before the first check on that bank,
/// so the user enters the stock's starting number).
/// </summary>
public sealed record GetRefundCheckOptions(Guid TenantId) : IQuery<RefundCheckOptions>;

internal sealed class GetRefundCheckOptionsHandler(DbContext db, IRefundCheckLedger ledger)
    : IQueryHandler<GetRefundCheckOptions, RefundCheckOptions>
{
    public async Task<RefundCheckOptions> Handle(GetRefundCheckOptions q, CancellationToken ct)
    {
        var funds = await ledger.GetRefundableAsync([q.TenantId], ct);
        var banks = funds.Select(f => f.BankAccountId).Distinct().ToArray();
        var highest = await db.Set<RefundCheck>().AsNoTracking()
            .Where(x => banks.Contains(x.BankAccountId))
            .GroupBy(x => x.BankAccountId)
            .Select(g => new { Bank = g.Key, Max = g.Max(x => x.CheckNumber) })
            .ToDictionaryAsync(x => x.Bank, x => x.Max, ct);
        return new RefundCheckOptions(funds
            .Select(f => new RefundCheckFund(f.Source, f.BankAccountId, f.PropertyId, f.OwnerId, f.Held,
                highest.TryGetValue(f.BankAccountId, out var max) ? max + 1 : null))
            .ToArray());
    }
}
