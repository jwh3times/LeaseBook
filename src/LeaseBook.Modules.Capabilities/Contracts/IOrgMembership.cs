namespace LeaseBook.Modules.Capabilities.Contracts;

/// <summary>
/// Consumer-owned port (ADR-007) answering whether a user belongs to an organization. Identity lives
/// in the host, so the interface is declared here in the module that needs the answer and satisfied
/// by a thin host adapter.
/// <para>
/// <b>This is a tenancy check, not a convenience.</b> <c>asp_net_users</c> is deliberately RLS-exempt
/// — login happens before organization context exists — so nothing in the database stops a cohort rule naming
/// a user from another organization. Both the user id and the org id are load-bearing: an id-only
/// lookup would admit a foreign organization's user into this org's cohort. Stating it as a port keeps the
/// requirement in this module's own language rather than leaking Identity's schema into it, and
/// keeps it from being something each caller has to remember.
/// </para>
/// </summary>
public interface IOrgMembership
{
    /// <summary>
    /// True when <paramref name="userId"/> exists AND belongs to <paramref name="orgId"/>. False for
    /// a user that does not exist and for one that belongs to a different organization — the caller
    /// must treat both the same way, because distinguishing them would be an existence oracle across
    /// an organization boundary.
    /// </summary>
    Task<bool> IsUserInOrgAsync(Guid userId, Guid orgId, CancellationToken ct = default);
}
