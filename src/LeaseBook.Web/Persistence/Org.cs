namespace LeaseBook.Web.Persistence;

/// <summary>
/// The tenant. Org is the unit of multi-tenancy and is itself <b>global-class</b> — it has no
/// <c>org_id</c> (it IS the org), so its table carries no RLS policy and is listed in the
/// schema-guard allowlist (§C.3). Host-owned for M0; the Directory module takes ownership in M2.
/// </summary>
public sealed class Org
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public DateTime CreatedAt { get; set; }
}
