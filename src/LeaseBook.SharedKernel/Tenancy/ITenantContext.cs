namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// The org the current unit of work belongs to. Request-scoped: set by the HTTP middleware from
/// the authenticated user's <c>org_id</c> claim, or by <see cref="OrgScopedExecutor"/> for jobs
/// and the seeder. <see langword="null"/> means <b>no context</b> — by design RLS then matches
/// nothing (fail closed). This is the ergonomic layer that drives the EF query filter and stamping;
/// the Postgres <c>SET LOCAL app.org_id</c> transaction is the actual security boundary (§C.4).
/// </summary>
public interface ITenantContext
{
    Guid? OrgId { get; }
}
