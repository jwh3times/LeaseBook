using FluentValidation;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Refunds;

/// <summary>
/// Batch status of refund-check entries (#473), derived — never stored by the check record:
/// <c>voided</c> when a linked reversal exists, otherwise the check's bank line clearance
/// (<c>outstanding</c> / <c>cleared</c> / <c>reconciled</c>). Entries that are not visible refund checks are
/// absent from the result.
/// </summary>
public sealed record GetRefundCheckStatuses(IReadOnlyCollection<Guid> EntryIds)
    : IQuery<IReadOnlyDictionary<Guid, RefundCheckStatus>>;

public sealed record RefundCheckStatus(string Status, Guid? VoidEntryId);

public sealed class GetRefundCheckStatusesValidator : AbstractValidator<GetRefundCheckStatuses>
{
    public GetRefundCheckStatusesValidator()
    {
        RuleFor(x => x.EntryIds).NotNull().Must(ids => ids.Count <= 1000)
            .WithMessage("Request at most 1000 entries at a time.");
    }
}

internal sealed class GetRefundCheckStatusesHandler(DbContext db)
    : IQueryHandler<GetRefundCheckStatuses, IReadOnlyDictionary<Guid, RefundCheckStatus>>
{
    public async Task<IReadOnlyDictionary<Guid, RefundCheckStatus>> Handle(
        GetRefundCheckStatuses query, CancellationToken ct)
    {
        var ids = query.EntryIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, RefundCheckStatus>();
        }

        var lines = await (
            from line in db.Set<JournalLine>().AsNoTracking()
            join entry in db.Set<JournalEntry>().AsNoTracking() on line.EntryId equals entry.Id
            where ids.Contains(line.EntryId) && line.AccountClass == AccountClass.TrustBank
                && entry.EventType == "RefundIssued"
                && entry.SourceRef != null && entry.SourceRef.StartsWith(RefundChecks.SourceRefPrefix)
            join state in db.Set<BankLineState>().AsNoTracking() on line.Id equals state.JournalLineId into states
            from state in states.DefaultIfEmpty()
            select new { line.EntryId, Status = state == null ? (BankLineStatus?)null : state.Status })
            .ToListAsync(ct);

        var reversals = await db.Set<JournalEntry>().AsNoTracking()
            .Where(e => e.ReversesEntryId != null && ids.Contains(e.ReversesEntryId.Value))
            .Select(e => new { Original = e.ReversesEntryId!.Value, e.Id })
            .ToDictionaryAsync(e => e.Original, e => e.Id, ct);

        return lines.ToDictionary(
            l => l.EntryId,
            l => reversals.TryGetValue(l.EntryId, out var voidId)
                ? new RefundCheckStatus("voided", voidId)
                : new RefundCheckStatus(
                    l.Status switch
                    {
                        BankLineStatus.Cleared => "cleared",
                        BankLineStatus.Reconciled => "reconciled",
                        _ => "outstanding",
                    },
                    null));
    }
}
