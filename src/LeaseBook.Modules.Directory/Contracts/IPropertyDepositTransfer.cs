namespace LeaseBook.Modules.Directory.Contracts;

/// <summary>
/// Consumer-owned write port for moving a property's held security-deposit responsibility when its
/// ownership changes. The host adapter delegates to Accounting on the ambient org transaction.
/// </summary>
public interface IPropertyDepositTransfer
{
    Task<Guid?> TransferAsync(
        Guid propertyId,
        Guid fromOwnerId,
        Guid toOwnerId,
        DateOnly effectiveDate,
        string sourceRef,
        CancellationToken ct);
}
