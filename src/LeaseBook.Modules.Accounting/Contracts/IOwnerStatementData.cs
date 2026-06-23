namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>Cross-module port (ADR-007/016): owner-statement financial data, batched by owner id.</summary>
public interface IOwnerStatementData
{
    Task<IReadOnlyDictionary<Guid, Features.Statements.OwnerStatement>> GetAsync(
        IReadOnlyList<Guid> ownerIds, Guid? propertyId, int year, int month, string basis, CancellationToken ct);
}
