namespace LeaseBook.Modules.Reporting.Contracts;

/// <summary>
/// Consumer-owned read port (ADR-007/016): owner and property display names needed to enrich a
/// <c>StatementView</c>. The host adapter delegates to Directory's <c>GetOwnerLookup</c> and
/// <c>GetPropertyLookup</c> queries via <c>ISender</c>. <b>Batch maps only</b> — never per-id.
/// </summary>
public interface IStatementNames
{
    /// <summary>Returns id → owner display name for all owners in the org.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetOwnerNamesAsync(CancellationToken ct);

    /// <summary>Returns id → property address for all non-system properties in the org.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetPropertyAddressesAsync(CancellationToken ct);
}
