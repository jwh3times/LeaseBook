using FluentValidation;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// The staff-only internal notes (#468) of a batch of entries, keyed by entry id; entries with no note
/// are absent. A <b>staff</b> read: the host uses it to overlay notes on the staff statement view without
/// the owner-statement read ever selecting <c>internal_note</c> — that read feeds the PDF, the CSV and the
/// issued artifact, so it stays note-free by construction.
/// </summary>
public sealed record GetEntryInternalNotes(IReadOnlyCollection<Guid> EntryIds)
    : IQuery<IReadOnlyDictionary<Guid, string>>;

public sealed class GetEntryInternalNotesValidator : AbstractValidator<GetEntryInternalNotes>
{
    public GetEntryInternalNotesValidator() => RuleFor(q => q.EntryIds).NotNull();
}

internal sealed class GetEntryInternalNotesHandler(DbContext db)
    : IQueryHandler<GetEntryInternalNotes, IReadOnlyDictionary<Guid, string>>
{
    public async Task<IReadOnlyDictionary<Guid, string>> Handle(GetEntryInternalNotes query, CancellationToken ct)
    {
        if (query.EntryIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = query.EntryIds.Distinct().ToArray();
        return await db.Set<JournalEntry>().AsNoTracking()
            .Where(e => ids.Contains(e.Id) && e.InternalNote != null)
            .ToDictionaryAsync(e => e.Id, e => e.InternalNote!, ct);
    }
}
