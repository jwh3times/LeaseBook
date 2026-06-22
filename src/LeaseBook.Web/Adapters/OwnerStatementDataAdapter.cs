using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Statements;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007/016) for the Reporting module's <see cref="IOwnerStatementData"/> port.
/// Delegates to the Accounting <see cref="GetOwnerStatementData"/> query handler via <see cref="ISender"/>
/// and returns a batch map (owner id → OwnerStatement) so the 23-owner run is one query, not N round-trips.
/// </summary>
internal sealed class OwnerStatementDataAdapter(ISender sender) : IOwnerStatementData
{
    public async Task<IReadOnlyDictionary<Guid, OwnerStatement>> GetAsync(
        IReadOnlyList<Guid> ownerIds, Guid? propertyId, int year, int month, string basis, CancellationToken ct)
    {
        var r = await sender.Query(new GetOwnerStatementData(ownerIds, propertyId, year, month, basis), ct);
        return r.ByOwner;
    }
}
