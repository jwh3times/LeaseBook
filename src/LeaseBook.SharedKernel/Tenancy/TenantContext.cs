namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// Mutable, request-scoped implementation of <see cref="ITenantContext"/>. Only the two sanctioned
/// entry points write <see cref="OrgId"/>: the HTTP unit-of-work middleware and
/// <see cref="OrgScopedExecutor"/>. Everything else consumes it read-only through
/// <see cref="ITenantContext"/>.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? OrgId { get; set; }
}
