namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// Mutable, request-scoped implementation of <see cref="IOrgContext"/>. Exactly one thing writes
/// <see cref="OrgId"/>: <see cref="OrgScopedExecutor"/>, which sets it as it opens the unit of work
/// and restores it as that work ends. The HTTP middleware used to be a second writer and is no
/// longer — it now goes through the executor like everything else. Everything else consumes this
/// read-only through <see cref="IOrgContext"/>. Keep it at one writer: a second would be able to
/// move the EF-layer view of the org without moving <c>app.org_id</c>, and the two silently
/// disagreeing is the failure this type exists to prevent.
/// </summary>
public sealed class OrgContext : IOrgContext
{
    public Guid? OrgId { get; set; }
}
