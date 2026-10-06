using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Banking;

/// <summary>
/// Which of the given journal lines a processor payout posted, and under which reference (ADR-053). A
/// batch read returning a map: a line no payout posted is absent. It reads the journal whatever the
/// line's date or clearance, so a caller can refuse to treat a payout's line as a line of its own even
/// when the line is outside every window it has read.
/// </summary>
public sealed record GetPayoutReferences(IReadOnlyCollection<Guid> JournalLineIds)
    : IQuery<IReadOnlyDictionary<Guid, string>>;

internal sealed class GetPayoutReferencesHandler(DbContext db)
    : IQueryHandler<GetPayoutReferences, IReadOnlyDictionary<Guid, string>>
{
    public async Task<IReadOnlyDictionary<Guid, string>> Handle(GetPayoutReferences query, CancellationToken ct)
    {
        var ids = query.JournalLineIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var rows = await (
            from line in db.Set<JournalLine>()
            join entry in db.Set<JournalEntry>() on line.EntryId equals entry.Id
            where ids.Contains(line.Id) && entry.SourceRef != null && entry.SourceRef.StartsWith(PayoutSourceRef.Prefix)
            select new { line.Id, entry.SourceRef }).ToListAsync(ct);

        return rows
            .Select(r => (r.Id, Reference: PayoutSourceRef.ReferenceOf(r.SourceRef)))
            .Where(r => r.Reference is not null)
            .ToDictionary(r => r.Id, r => r.Reference!);
    }
}
