using FluentValidation;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// Resolves canonical rent source references to unreversed obligations that remain open under
/// oldest-charge-first receivable allocation at the assessment date.
/// </summary>
public sealed record GetRentObligationEntries(IReadOnlyList<string> SourceRefs, DateOnly AsOf)
    : IQuery<RentObligationEntriesResponse>;

public sealed record RentObligationEntriesResponse(IReadOnlyList<RentObligationEntryRow> Rows);

public sealed record RentObligationEntryRow(Guid EntryId, string SourceRef, Guid TenantId);

internal sealed class GetRentObligationEntriesValidator : AbstractValidator<GetRentObligationEntries>
{
    public GetRentObligationEntriesValidator()
    {
        RuleFor(x => x.SourceRefs).NotEmpty();
        RuleFor(x => x.AsOf).NotEmpty();
    }
}

internal sealed class GetRentObligationEntriesHandler(DbContext db)
    : IQueryHandler<GetRentObligationEntries, RentObligationEntriesResponse>
{
    public async Task<RentObligationEntriesResponse> Handle(
        GetRentObligationEntries query,
        CancellationToken ct)
    {
        var sourceRefs = query.SourceRefs.ToHashSet(StringComparer.Ordinal);
        var allocation = await ReceivableAllocationReader.ReadAsync(db, query.AsOf, ct);
        var rows = allocation.OpenCharges
            .Where(charge =>
                charge.EventType == "RentCharged"
                && charge.SourceRef is not null
                && sourceRefs.Contains(charge.SourceRef))
            .Select(charge => new RentObligationEntryRow(
                charge.EntryId,
                charge.SourceRef!,
                charge.TenantId))
            .ToList();

        return new RentObligationEntriesResponse(rows);
    }
}
