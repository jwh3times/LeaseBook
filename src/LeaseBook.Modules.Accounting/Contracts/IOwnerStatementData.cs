namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>Cross-module port (ADR-007/016): owner-statement financial data, batched by owner id.</summary>
public interface IOwnerStatementData
{
    /// <param name="anchors">
    /// Per owner, the statement issued for the preceding period, from which this one carries forward
    /// (ADR-045). Null or absent for an owner means no carry-forward for that owner.
    /// </param>
    Task<IReadOnlyDictionary<Guid, Features.Statements.OwnerStatement>> GetAsync(
        IReadOnlyList<Guid> ownerIds, Guid? propertyId, int year, int month, string basis,
        IReadOnlyDictionary<Guid, Features.Statements.StatementAnchor>? anchors, CancellationToken ct);
}
