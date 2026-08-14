using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Tenancy;

/// <summary>
/// The ONLY place <c>app.platform</c> is ever set (ADR-028). Opens one transaction, sets the GUC
/// with <c>set_config(..., is_local =&gt; true)</c>, runs the work, commits (rollback on failure).
/// It is the platform-plane mirror of <see cref="SharedKernel.Tenancy.OrgScopedExecutor"/>.
/// <para>
/// <b>Why this lives in the host and not in <c>SharedKernel</c>:</b> containment, not purity. The
/// often-repeated reason — "SharedKernel stays pure cross-cutting primitives" — does not survive
/// contact with <c>OrgScopedExecutor</c>, which is a scoped executor living in <c>SharedKernel</c>
/// and is why that project references EF Relational at all. The real reason is that every feature
/// module references <c>SharedKernel</c>, so a platform executor there would be nameable from every
/// module. Here it is nameable from none: a module that needs the platform plane must declare a
/// port and be satisfied by a host adapter (ADR-007), which is what keeps the escape to one
/// auditable call site. Do not "simplify" this by moving it next to its sibling.
/// </para>
/// <para>
/// SET LOCAL, never session-level — the setting must not survive the transaction, because Npgsql
/// pools connections and a leaked platform scope would disable org isolation for whatever request
/// picked up that connection next. This is the same rule <c>OrgScopedExecutor</c> follows for
/// <c>app.org_id</c>, and the shared bracket in <see cref="TransactionalUnitOfWork"/> — which knows
/// no GUC name — is what both planes have in common.
/// </para>
/// <para>
/// What the GUC unlocks: on <c>entitlements</c> and <c>capability_cohorts</c> it is the <i>only</i>
/// way to satisfy the <c>{table}_platform_write</c> policy — an organization-plane transaction may read its
/// own rows but every write is rejected. On <c>feature_flags</c> and <c>platform_audit_events</c> it
/// gates reads as well. It does NOT widen ordinary org-scoped tables: those carry the plain
/// <c>EnableOrgRls</c> policy, which never mentions <c>app.platform</c>. Opening platform scope is
/// therefore an escape for four named tables, not a general bypass.
/// </para>
/// <para>
/// Do not add a second caller of set_config('app.platform', ...) anywhere — the escape stays
/// greppable and auditable only while there is exactly one. <c>PlatformScopeCallSiteTests</c> in
/// LeaseBook.Tests.Architecture fails the build if a second one appears.
/// </para>
/// </summary>
public sealed class PlatformScopedExecutor(DbContext db)
{
    public Task RunAsync(Func<Task> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        return RunAsync(async () =>
        {
            await work();
            return (object?)null;
        }, ct);
    }

    /// <summary>Value-returning form. See <see cref="RunAsync(Func{Task}, CancellationToken)"/>.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        return TransactionalUnitOfWork.RunAsync(
            db,
            token => db.Database.ExecuteSqlAsync($"SELECT set_config('app.platform', 'on', true)", token),
            work,
            ct);
    }
}
