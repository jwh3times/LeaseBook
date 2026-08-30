using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Migration;

/// <summary>
/// Returns the one accounting date carried by ADR-020 per-position opening entries. No entries means
/// the organization has not established a cutover date yet; more than one date means historical
/// opening positions are inconsistent and migration work must fail closed.
/// </summary>
public sealed record GetOpeningCutoverDate : IQuery<OpeningCutoverDateResponse>;

public sealed record OpeningCutoverDateResponse(DateOnly? CutoverDate);

internal sealed class GetOpeningCutoverDateHandler(DbContext db)
    : IQueryHandler<GetOpeningCutoverDate, OpeningCutoverDateResponse>
{
    public async Task<OpeningCutoverDateResponse> Handle(
        GetOpeningCutoverDate query,
        CancellationToken ct)
    {
        var dates = await db.Set<JournalEntry>()
            .AsNoTracking()
            .Where(entry => entry.EventType == "OpeningBalance")
            .Select(entry => entry.EntryDate)
            .Distinct()
            .OrderBy(date => date)
            .Take(2)
            .ToListAsync(ct);

        if (dates.Count > 1)
        {
            throw new InconsistentOpeningCutoverDatesException();
        }

        return new OpeningCutoverDateResponse(dates.Count == 0 ? null : dates[0]);
    }
}

/// <summary>
/// Existing ADR-020 opening entries carry more than one accounting date. This state cannot be
/// reconciled automatically because changing posted dates requires linked reversals or reprovisioning.
/// </summary>
public sealed class InconsistentOpeningCutoverDatesException()
    : Exception("Opening balances use more than one cutover date. Re-provision the migration organization before continuing.");
