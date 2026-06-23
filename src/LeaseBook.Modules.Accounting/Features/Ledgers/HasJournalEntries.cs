using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// True iff the current org has ≥1 journal entry — i.e. any posted financial activity exists
/// (RLS-scoped). Reads Accounting's own <c>journal_entries</c> table, so it belongs here; the host
/// onboarding-status endpoint (M7 WP-5) dispatches it via <see cref="ISender"/> to decide whether the
/// import-first wizard should take over an empty dashboard — an org with operational data is never
/// hijacked into onboarding even when it never used the import wizard (e.g. the seeded demo org).
/// </summary>
public sealed record HasJournalEntries : IQuery<bool>;

internal sealed class HasJournalEntriesHandler(DbContext db) : IQueryHandler<HasJournalEntries, bool>
{
    public Task<bool> Handle(HasJournalEntries query, CancellationToken ct) =>
        db.Set<JournalEntry>().AnyAsync(ct);
}
