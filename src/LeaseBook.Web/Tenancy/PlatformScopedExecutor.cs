using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Tenancy;

/// <summary>
/// The ONLY place <c>app.platform</c> is ever set (ADR-028). Opens one transaction, sets the GUC
/// with <c>set_config(..., is_local =&gt; true)</c>, runs the work, commits (rollback on failure).
/// It is the platform-plane mirror of <see cref="SharedKernel.Tenancy.OrgScopedExecutor"/> and lives
/// in the host rather than SharedKernel, which stays pure cross-cutting primitives.
/// <para>
/// SET LOCAL, never session-level — the setting must not survive the transaction, because Npgsql
/// pools connections and a leaked platform scope would disable org isolation for whatever request
/// picked up that connection next. This is the same rule <c>OrgScopedExecutor</c> follows for
/// <c>app.org_id</c>.
/// </para>
/// <para>
/// What the GUC unlocks: on <c>entitlements</c> and <c>capability_cohorts</c> it is the <i>only</i>
/// way to satisfy the <c>{table}_platform_write</c> policy — a tenant-plane transaction may read its
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
    public async Task RunAsync(Func<Task> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT set_config('app.platform', 'on', true)", ct);

            await work();

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
