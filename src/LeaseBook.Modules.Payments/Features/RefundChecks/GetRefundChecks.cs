using FluentValidation;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Payments.Features.RefundChecks;

/// <summary>
/// Refund checks, newest first, filtered by bank account and/or tenant (#473), one page at a time with
/// the total across every page (#476). <see cref="Status"/> <c>outstanding</c> keeps only checks the bank
/// has not cleared and that were not voided, so an old uncleared check is never pushed out of reach.
/// </summary>
public sealed record GetRefundChecks(
    Guid? BankAccountId = null, Guid? TenantId = null, Guid? Id = null, string? Status = null, int Page = 1,
    int PageSize = 50) : IQuery<RefundCheckPage>;

public sealed record RefundCheckPage(IReadOnlyList<RefundCheckView> Items, int Total, int Page, int PageSize);

public sealed class GetRefundChecksValidator : AbstractValidator<GetRefundChecks>
{
    public GetRefundChecksValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 200);
        RuleFor(q => q.Status).Must(s => s is null or RefundCheckReads.Outstanding)
            .WithMessage("Status must be 'outstanding' or omitted.");
    }
}

internal sealed class GetRefundChecksHandler(DbContext db, IRefundCheckLedger ledger)
    : IQueryHandler<GetRefundChecks, RefundCheckPage>
{
    public async Task<RefundCheckPage> Handle(GetRefundChecks q, CancellationToken ct)
    {
        var query = db.Set<RefundCheck>().AsNoTracking();
        if (q.BankAccountId is { } bank) { query = query.Where(x => x.BankAccountId == bank); }
        if (q.TenantId is { } tenant) { query = query.Where(x => x.TenantId == tenant); }
        if (q.Id is { } id) { query = query.Where(x => x.Id == id); }
        // Id breaks the tie a tenant's list can have (two banks, one date, one number), so pages never
        // repeat or skip a check.
        query = query.OrderByDescending(x => x.IssueDate).ThenByDescending(x => x.CheckNumber)
            .ThenByDescending(x => x.Id);
        var skip = (q.Page - 1) * q.PageSize;

        if (q.Status is null)
        {
            var total = await query.CountAsync(ct);
            var page = await query.Skip(skip).Take(q.PageSize).ToListAsync(ct);
            return new RefundCheckPage(await RefundCheckReads.ViewsAsync(db, ledger, page, ct), total, q.Page, q.PageSize);
        }

        // Status is derived from the journal (cleared, reconciled, voided), not stored on the check, so the
        // filter reads every candidate's status in one batch and pages what is left. Refund checks are few
        // per bank, which keeps the candidate set small.
        var candidates = await query.Select(x => new { x.Id, x.EntryId }).ToListAsync(ct);
        var statuses = await ledger.GetStatusesAsync(candidates.Select(c => c.EntryId).ToArray(), ct);
        var outstanding = candidates
            .Where(c => RefundCheckReads.StatusOf(statuses, c.EntryId) == RefundCheckReads.Outstanding)
            .Select(c => c.Id)
            .ToList();
        var pageIds = outstanding.Skip(skip).Take(q.PageSize).ToList();
        var rows = await db.Set<RefundCheck>().AsNoTracking().Where(x => pageIds.Contains(x.Id)).ToListAsync(ct);
        var ordered = rows.OrderBy(x => pageIds.IndexOf(x.Id)).ToList();
        return new RefundCheckPage(
            await RefundCheckReads.ViewsAsync(db, ledger, ordered, ct, statuses), outstanding.Count, q.Page, q.PageSize);
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
