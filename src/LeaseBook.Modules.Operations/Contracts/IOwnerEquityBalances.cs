namespace LeaseBook.Modules.Operations.Contracts;

/// <summary>
/// Read-direction cross-module port (ADR-007 / WP-4). Operations declares the interface;
/// the host adapter (<c>OwnerEquityBalancesAdapter</c>) implements it by dispatching
/// Accounting's <c>GetOwnerBalances</c> query via <see cref="LeaseBook.SharedKernel.Cqrs.ISender"/>
/// and returning only the cash-basis operating equity for the requested owner ids.
/// <para>
/// <b>Basis parameter:</b> accepted for interface conformance; the underlying Accounting query
/// always returns cash+both equity (the distributable cash balance per §C.6/P30).
/// </para>
/// </summary>
public interface IOwnerEquityBalances
{
    /// <summary>
    /// Returns a map of owner id → cash-basis equity for the given owner ids.
    /// Owners with no journal activity are absent from the map (treat as zero equity).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetAsync(
        IReadOnlyList<Guid> ownerIds, string basis, CancellationToken ct);
}
