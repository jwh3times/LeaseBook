using System.Diagnostics;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Posting;

/// <summary>
/// Posts a linked reversal for an existing entry (WP-04). Loads the original org-scoped, refuses to
/// reverse an already-reversed entry or a reversal itself, then builds the mirror (debit/credit
/// swapped, dimensions and basis preserved) and posts it through <see cref="IPostingService"/> dated
/// <c>asOfDate</c> — so the correction lands in the open period, never the original's locked one.
/// </summary>
internal sealed class ReversalService(DbContext db, ITenantContext tenant, IPostingService posting) : IReversalService
{
    public async Task<Guid> ReverseAsync(Guid entryId, string reason, DateOnly asOfDate, CancellationToken ct)
    {
        if (tenant.OrgId is null)
        {
            throw new InvalidOperationException("ReverseAsync requires an ambient org context.");
        }

        var original = await db.Set<JournalEntry>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == entryId, ct)
            ?? throw new InvalidOperationException($"Entry {entryId} was not found in the current org.");

        // A reversal cannot itself be reversed.
        if (original.ReversesEntryId is not null)
        {
            throw new AlreadyReversedException($"Entry {entryId} is a reversal and cannot be reversed.");
        }

        // An entry is reversed at most once (the partial unique index is the backstop in PostingService).
        var alreadyReversed = await db.Set<JournalEntry>().AsNoTracking()
            .AnyAsync(e => e.ReversesEntryId == entryId, ct);
        if (alreadyReversed)
        {
            throw new AlreadyReversedException($"Entry {entryId} has already been reversed.");
        }

        // Mirror lines, resolving each account's code so the reversal posts through the same code-based
        // path. Debit and credit are swapped; basis, dimensions, and memo are preserved.
        var mirrored = await (
            from line in db.Set<JournalLine>().AsNoTracking()
            join account in db.Set<Account>().AsNoTracking() on line.AccountId equals account.Id
            where line.EntryId == entryId
            select new PostLineRequest(
                account.Code,
                line.Credit,
                line.Debit,
                line.Basis,
                line.PropertyId,
                line.UnitId,
                line.OwnerId,
                line.TenantId,
                line.BankAccountId,
                line.Memo))
            .ToListAsync(ct);

        var request = new PostEntryRequest(
            asOfDate,
            EventTypes.EntryVoided,
            EventSubtype: null,
            Description: $"VOID: {reason}",
            SourceRef: null,
            Lines: mirrored,
            ReversesEntryId: entryId);

        var reversalId = await posting.PostAsync(request, ct);

        Activity.Current?.AddEvent(new ActivityEvent(
            "accounting.entry_voided",
            tags: new ActivityTagsCollection
            {
                { "entry_id", reversalId },
                { "reverses_entry_id", entryId },
            }));

        return reversalId;
    }
}
