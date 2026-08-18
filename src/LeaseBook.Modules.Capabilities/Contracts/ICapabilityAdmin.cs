namespace LeaseBook.Modules.Capabilities.Contracts;

/// <summary>
/// Changing platform capability state (ADR-028). The counterpart to <see cref="ICapabilityGate"/>,
/// which answers what the state currently is.
/// <para>
/// <b>Every member is one committed change.</b> Each opens its own platform-scoped transaction, takes
/// its timestamp from Postgres, writes the state row and its <c>platform_audit_events</c> row in a
/// single <c>SaveChanges</c>, and publishes the invalidation — all of it inside that transaction, so
/// a refusal leaves neither a state row nor an audit row. A caller supplies what changed and who is
/// accountable; it does not supply, and cannot omit, any of the rest.
/// </para>
/// <para>
/// <b>Opening the scope is deliberately not the caller's job, and that has a structural consequence.</b>
/// <see cref="IPlatformScope"/> opens its own transaction, and a scoped unit of work refuses to nest,
/// so this interface cannot be called from inside a request transaction — which every authenticated
/// request already is. ADR-028 §14's "no endpoint, no UI" therefore holds by construction rather than
/// by anyone remembering it. Out-of-band callers (the CLI verb, a job) resolve their own scope and are
/// unaffected.
/// </para>
/// <para>
/// <b>Refusals.</b> Domain refusals raise <see cref="CapabilityRefusedException"/> with a message fit
/// to show an operator. A capability absent from the registry, or one marked a fixture, is refused
/// here as well as at the CLI: the CLI's check exists to give a good message at the moment of the
/// typo, this one exists so the rule holds for every caller. Provider-level failures surface as the
/// database raised them — mapping those to prose needs the Npgsql dependency this module does not
/// have, and belongs to the host.
/// </para>
/// </summary>
public interface ICapabilityAdmin
{
    /// <summary>Turns a deployment-wide flag on, recording the value it overwrote.</summary>
    Task<DateTime> EnableFlagAsync(string capability, string actor, CancellationToken ct = default);

    /// <summary>Turns a deployment-wide flag off — an explicit kill, not the same as no row.</summary>
    Task<DateTime> DisableFlagAsync(string capability, string actor, CancellationToken ct = default);

    /// <summary>
    /// Removes the explicit override so resolution falls through to cohort state and the registry
    /// default. Refuses when no override exists: a no-op reported as success tells an operator the
    /// override is gone when a typo actually matched nothing.
    /// </summary>
    Task<DateTime> ClearFlagAsync(string capability, string actor, CancellationToken ct = default);

    /// <summary>
    /// Appends a grant event for one organization. Entitlements are append-only — a re-grant appends
    /// another event rather than being a no-op, because the trail records what an operator did.
    /// </summary>
    Task<DateTime> GrantAsync(string capability, Guid orgId, string actor, CancellationToken ct = default);

    /// <summary>Appends a revoke event. There is no row to update; current state is the latest event.</summary>
    Task<DateTime> RevokeAsync(string capability, Guid orgId, string actor, CancellationToken ct = default);

    /// <summary>
    /// Adds a cohort rule — org-wide when <paramref name="userId"/> is null, otherwise for that user.
    /// A named user is checked against the named organization first: <c>asp_net_users</c> is
    /// RLS-exempt, so nothing in the database would stop a rule naming another organization's user.
    /// </summary>
    Task<DateTime> AddToCohortAsync(
        string capability, Guid orgId, Guid? userId, string actor, CancellationToken ct = default);

    /// <summary>
    /// Removes the rule an add with the same arguments would have created. Refuses when nothing
    /// matches, because the likely cause is a mistyped org and "removed 0 rules" reported as success
    /// is how an operator concludes a cohort is gone when it is not.
    /// </summary>
    Task<DateTime> RemoveFromCohortAsync(
        string capability, Guid orgId, Guid? userId, string actor, CancellationToken ct = default);
}
