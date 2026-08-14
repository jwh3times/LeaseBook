using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter for Directory's property-deposit transfer port. Both commands run inside the same
/// request transaction, so the journal reattribution and Directory owner change commit atomically.
/// </summary>
internal sealed class PropertyDepositTransferAdapter(ISender sender) : IPropertyDepositTransfer
{
    public Task<Guid?> TransferAsync(
        Guid propertyId,
        Guid fromOwnerId,
        Guid toOwnerId,
        DateOnly effectiveDate,
        string sourceRef,
        CancellationToken ct) =>
        sender.Send(new TransferPropertyDeposits(
            propertyId, fromOwnerId, toOwnerId, effectiveDate, sourceRef), ct);
}
