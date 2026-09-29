using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// The staff statement view's note overlay (#468): each entry on the statement that carries a staff-only
/// internal note, keyed by the entry id its <see cref="StatementLineView"/> or
/// <see cref="CarryForwardLineView"/> already has. Deliberately <b>not</b> part of
/// <see cref="StatementView"/>: that record is what is rendered to PDF and CSV and issued to the owner, so
/// the note travels on its own staff-only route and cannot reach the owner's copy.
/// </summary>
public sealed record StatementInternalNotesResponse(IReadOnlyList<StatementInternalNote> Notes);

/// <summary>One statement entry's staff-only note. Not on the owner's copy.</summary>
public sealed record StatementInternalNote(Guid EntryId, string InternalNote);

/// <summary>Reads the overlay for an assembled statement: one batch read over its entry ids.</summary>
public static class StatementInternalNotes
{
    public static async Task<StatementInternalNotesResponse> ReadAsync(
        StatementView view, ISender sender, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);

        // Statement order: section lines first, then the carry-forward's prior-period adjustments.
        var entryIds = view.Sections.SelectMany(s => s.Lines).Select(l => l.EntryId)
            .Concat(view.CarryForward?.Lines.Select(l => l.EntryId) ?? [])
            .Distinct()
            .ToList();

        var notes = await sender.Query(new GetEntryInternalNotes(entryIds), ct);
        return new StatementInternalNotesResponse(entryIds
            .Where(notes.ContainsKey)
            .Select(id => new StatementInternalNote(id, notes[id]))
            .ToList());
    }
}
