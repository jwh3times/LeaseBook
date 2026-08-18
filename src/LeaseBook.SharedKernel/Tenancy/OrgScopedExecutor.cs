using Microsoft.EntityFrameworkCore;

namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// The entry point for org-scoped database work — requests (through the HTTP middleware), jobs, the
/// seeder, the CLI, and cross-org system sweeps that loop org-by-org. Open one transaction, set
/// <c>app.org_id</c> inside it via <c>set_config(..., is_local => true)</c>, run the work, commit
/// (rollback on failure). The <c>SET LOCAL</c> only survives inside this transaction — that is what
/// makes Npgsql connection pooling safe (§C.4). Missing context is never allowed to silently return
/// empty rows: a <see cref="Guid.Empty"/> org id throws <b>before</b> any database access.
/// <para>
/// <b>Every call states who is accountable.</b> There is no actor-less overload, deliberately: this
/// executor writes <see cref="ActorContext"/>, which stamps <c>actor_kind</c> plus either the user
/// column or <c>actor_process</c> on <c>journal_entries</c> and <c>audit_events</c>, and a default
/// would mean an unattributed fiduciary write could happen by saying nothing. Use
/// <see cref="RunAsync{T}(Guid, Actor, Func{Task{T}}, CancellationToken)"/> when a user is
/// accountable and <see cref="RunAsSystemAsync{T}(Guid, string, Func{Task{T}}, CancellationToken)"/>
/// when a named process is. Since ADR-039 the two are distinguishable in the persisted row, not only
/// at the call site — a system write records which process wrote it.
/// </para>
/// <para>
/// The transaction bracket itself — including the refusal to nest — lives in
/// <see cref="TransactionalUnitOfWork"/>, shared with the platform plane. Only the one
/// <c>set_config</c> line below is specific to this plane.
/// </para>
/// </summary>
public sealed class OrgScopedExecutor(
    DbContext db, OrgContext orgContext, ActorContext actorContext)
{
    /// <summary>Runs <paramref name="work"/> for <paramref name="orgId"/>, attributed to <paramref name="actor"/>.</summary>
    public Task RunAsync(Guid orgId, Actor actor, Func<Task> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        return RunAsync(
            orgId,
            actor,
            async () =>
            {
                await work();
                return (object?)null;
            },
            ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/> as the system, acting as the named <paramref name="process"/>.
    /// The name is persisted, so it must be a stable process identifier — see
    /// <see cref="Actor.System"/>, which rejects anything else.
    /// </summary>
    public Task RunAsSystemAsync(
        Guid orgId, string process, Func<Task> work, CancellationToken ct = default) =>
        RunAsync(orgId, Actor.System(process), work, ct);

    /// <inheritdoc cref="RunAsSystemAsync(Guid, string, Func{Task}, CancellationToken)"/>
    public Task<T> RunAsSystemAsync<T>(
        Guid orgId, string process, Func<Task<T>> work, CancellationToken ct = default) =>
        RunAsync(orgId, Actor.System(process), work, ct);

    /// <summary>Value-returning form. See <see cref="RunAsync(Guid, Actor, Func{Task}, CancellationToken)"/>.</summary>
    public async Task<T> RunAsync<T>(
        Guid orgId, Actor actor, Func<Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(work);

        // Before the nesting check and before any database access: an empty org id is a caller
        // mistake, and OrgIsolationTests pins that it is reported without touching the database.
        if (orgId == Guid.Empty)
        {
            throw new ArgumentException(
                "OrgScopedExecutor requires a non-empty org id — running org-scoped work with no " +
                "context would let RLS silently return empty results.", nameof(orgId));
        }

        var previousOrg = orgContext.OrgId;
        var previousActor = actorContext.Actor;
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
                    orgContext.OrgId = orgId;
                    actorContext.Actor = actor;
                },
                work,
                ct);
        }
        finally
        {
            // Both mirror the DB's transaction-local context: they die with the unit of work.
            orgContext.OrgId = previousOrg;
            actorContext.Actor = previousActor;
        }
    }
}
