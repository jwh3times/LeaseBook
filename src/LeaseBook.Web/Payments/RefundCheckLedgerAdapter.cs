using LeaseBook.Modules.Accounting.Features.Refunds;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Payments;

/// <summary>Delegates Payments' refund-check ledger port to Accounting's refund commands (ADR-007, #473).</summary>
internal sealed class RefundCheckLedgerAdapter(ISender sender, TimeProvider clock) : IRefundCheckLedger
{
    public async Task<IReadOnlyList<RefundableFunds>> GetRefundableAsync(
        IReadOnlyCollection<Guid> tenantIds, CancellationToken ct) =>
        (await sender.Query(new GetRefundableBalances(tenantIds), ct))
            .Select(b => new RefundableFunds(b.TenantId, b.Source, b.BankAccountId, b.PropertyId, b.OwnerId, b.Held))
            .ToArray();

    public async Task<RefundPosting> PostAsync(RefundPostingRequest r, CancellationToken ct)
    {
        var posted = await sender.Send(new PostRefundCheck(
            r.TenantId, r.Source, r.Amount, r.Date, r.CheckId, r.CheckNumber,
            r.Bucket is { } b ? new RefundBucket(b.BankAccountId, b.PropertyId, b.OwnerId) : null,
            r.InternalNote), ct);
        return new RefundPosting(posted.EntryId, posted.BankAccountId);
    }

    public async Task<Guid> VoidAsync(Guid entryId, string reason, CancellationToken ct) =>
        (await sender.Send(new VoidRefundCheck(entryId, reason, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)), ct))
        .EntryId;

    public async Task<IReadOnlyDictionary<Guid, RefundLedgerStatus>> GetStatusesAsync(
        IReadOnlyCollection<Guid> entryIds, CancellationToken ct) =>
        (await sender.Query(new GetRefundCheckStatuses(entryIds), ct))
            .ToDictionary(x => x.Key, x => new RefundLedgerStatus(x.Value.Status, x.Value.VoidEntryId));
}
