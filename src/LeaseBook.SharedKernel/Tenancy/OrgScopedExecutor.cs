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
/// <c>set_config</c> statement below is specific to this plane.
/// </para>
/// <para>
/// <b>It is the only production setter of <c>app.org_id</c>, <c>app.persona</c> and
/// <c>app.user_id</c></b> (#314, ADR-048), and sets all three in one statement, transaction-locally.
/// The persona is the database boundary inside an organization: a restrictive policy on every
/// org-scoped table admits <c>staff</c> and <c>system</c> organization-wide, a portal persona only
/// through its explicit grants, and anything else not at all. <c>OrgContextCallSiteTests</c> fails the
/// build on a second setter.
/// </para>
/// </summary>
public sealed class OrgScopedExecutor(
    DbContext db, OrgContext orgContext, ActorContext actorContext)
{
    /// <summary>
    /// Runs <paramref name="work"/> for <paramref name="orgId"/>, attributed to <paramref name="actor"/>
    /// and confined to what <paramref name="persona"/> may reach (#314, ADR-048).
    /// <para>
    /// The persona is stated, never inferred, for the same reason the actor is: a default would mean
    /// a unit of work could reach the whole organization by saying nothing. System work states it by
    /// calling <see cref="RunAsSystemAsync(Guid, string, Func{Task}, CancellationToken)"/>; a request
    /// states it from the principal's roles, resolved by the host before the transaction opens.
    /// </para>
    /// </summary>
    public Task RunAsync(Guid orgId, Actor actor, Persona persona, Func<Task> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        return RunAsync(
            orgId,
            actor,
            persona,
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
    /// <see cref="Actor.System"/>, which rejects anything else. System work is organization-wide:
    /// it runs under <see cref="Persona.System"/>.
    /// </summary>
    public Task RunAsSystemAsync(
        Guid orgId, string process, Func<Task> work, CancellationToken ct = default) =>
        RunAsync(orgId, Actor.System(process), Persona.System, work, ct);

    /// <inheritdoc cref="RunAsSystemAsync(Guid, string, Func{Task}, CancellationToken)"/>
    public Task<T> RunAsSystemAsync<T>(
        Guid orgId, string process, Func<Task<T>> work, CancellationToken ct = default) =>
        RunAsync(orgId, Actor.System(process), Persona.System, work, ct);

    /// <summary>Value-returning form. See <see cref="RunAsync(Guid, Actor, Persona, Func{Task}, CancellationToken)"/>.</summary>
    public async Task<T> RunAsync<T>(
        Guid orgId, Actor actor, Persona persona, Func<Task<T>> work, CancellationToken ct = default)
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

        var personaValue = persona.ToDbValue();

        // The system persona is organization-wide, so a user can never hold it: that would let a
        // request reach every row by being mislabelled. A portal persona, conversely, is resolved
        // at the database from app.user_id, so without a user it could only ever see nothing —
        // stating one for a system actor is a caller mistake worth naming here.
        if (persona == Persona.System && !actor.IsSystem)
        {
            throw new ArgumentException(
                $"A user actor ({actor}) cannot run under the system persona. Resolve the persona " +
                "from the user's roles; only a named system process is organization-wide by fiat.",
                nameof(persona));
        }

        if (persona.IsPortal() && actor.IsSystem)
        {
            throw new ArgumentException(
                $"The {personaValue} persona needs a user: its rows are granted through that user's " +
                $"portal link, and a system actor ({actor}) has none.", nameof(persona));
        }

        var previousOrg = orgContext.OrgId;
        var previousActor = actorContext.Actor;
        try
        {
            return await TransactionalUnitOfWork.RunAsync(
                db,
                async token =>
                {
                    // Parameterized equivalent of `SET LOCAL app.org_id = '<uuid>'` plus the persona
                    // pair, in one round trip. Bound as text because set_config's value argument is
                    // text; the policies cast org and user back with ::uuid. app.user_id is written
                    // as '' for a system actor, which every policy maps to NULL — fail closed.
                    await db.Database.ExecuteSqlAsync(
                        $"""
                         SELECT set_config('app.org_id', {orgId.ToString()}, true),
                                set_config('app.persona', {personaValue}, true),
                                set_config('app.user_id', {actor.UserId?.ToString() ?? string.Empty}, true)
                         """,
                        token);
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
