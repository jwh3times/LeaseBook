namespace LeaseBook.Modules.Operations.Domain;

/// <summary>
/// The kind of entity that a <see cref="BulkRunItem"/> targets — used to build the
/// <c>source_ref</c> key and to route preview rows.
/// </summary>
public enum RunTargetKind
{
    /// <summary>Rent and late-fee runs target individual leases.</summary>
    Lease,

    /// <summary>Disbursement runs target owners.</summary>
    Owner,
}
