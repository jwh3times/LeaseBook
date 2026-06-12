using LeaseBook.SharedKernel.Tenancy;

namespace LeaseBook.Web.Tenancy;

/// <summary>
/// The "no org" context used when an <see cref="Persistence.AppDbContext"/> is constructed outside
/// DI (design-time migrations, hand-built test contexts). Its <see cref="OrgId"/> is always null, so
/// the EF query filter on org-scoped entities matches nothing — the same fail-closed behavior the
/// database enforces when <c>app.org_id</c> is unset.
/// </summary>
internal sealed class NullTenantContext : ITenantContext
{
    public static readonly NullTenantContext Instance = new();

    private NullTenantContext()
    {
    }

    public Guid? OrgId => null;
}
