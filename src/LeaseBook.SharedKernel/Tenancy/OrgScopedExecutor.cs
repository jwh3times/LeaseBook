using Microsoft.EntityFrameworkCore;

namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// The non-HTTP entry point for org-scoped database work (jobs, the seeder, cross-org system
/// sweeps that loop org-by-org). It is the scheduler-agnostic counterpart of the request
/// middleware: open one transaction, set <c>app.org_id</c> inside it via
/// <c>set_config(..., is_local => true)</c>, run the work, commit (rollback on failure). The
/// <c>SET LOCAL</c> only survives inside this transaction — that is what makes Npgsql connection
/// pooling safe (§C.4). Missing context is never allowed to silently return empty rows: a
/// <see cref="Guid.Empty"/> org id throws <b>before</b> any database access.
/// <para>
/// The transaction bracket itself — including the refusal to nest — lives in
/// <see cref="TransactionalUnitOfWork"/>, shared with the platform plane. Only the one
/// <c>set_config</c> line below is specific to this plane.
/// </para>
/// </summary>
public sealed class OrgScopedExecutor(DbContext db, TenantContext tenantContext)
{
    public Task RunAsync(Guid orgId, Func<Task> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        return RunAsync(
            orgId,
            async () =>
            {
                await work();
                return (object?)null;
            },
            ct);
    }

    /// <summary>Value-returning form. See <see cref="RunAsync(Guid, Func{Task}, CancellationToken)"/>.</summary>
    public async Task<T> RunAsync<T>(Guid orgId, Func<Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        // Before the nesting check and before any database access: an empty org id is a caller
        // mistake, and TenantIsolationTests pins that it is reported without touching the database.
        if (orgId == Guid.Empty)
        {
            throw new ArgumentException(
                "OrgScopedExecutor requires a non-empty org id — running org-scoped work with no " +
                "context would let RLS silently return empty results.", nameof(orgId));
        }

        var previous = tenantContext.OrgId;
        try
        {
            return await TransactionalUnitOfWork.RunAsync(
                db,
                async token =>
                {
                    // Parameterized equivalent of `SET LOCAL app.org_id = '<uuid>'`. Bound as text
                    // because set_config's value argument is text; the RLS policy casts it back
                    // with ::uuid.
                    await db.Database.ExecuteSqlAsync(
                        $"SELECT set_config('app.org_id', {orgId.ToString()}, true)", token);
                    tenantContext.OrgId = orgId;
                },
                work,
                ct);
        }
        finally
        {
            // Context is transaction-local in the DB; mirror that for the EF-layer ergonomics.
            tenantContext.OrgId = previous;
        }
    }
}
