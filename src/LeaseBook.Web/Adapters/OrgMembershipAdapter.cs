using LeaseBook.Modules.Capabilities.Contracts;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter for the Capabilities module's <see cref="IOrgMembership"/> port (ADR-007).
/// <para>
/// Unlike the cross-module adapters beside it, this one reads directly rather than dispatching
/// through <c>ISender</c>: <see cref="AppUser"/> is ASP.NET Identity, owned by the host, not another
/// feature module — there is no module on the far side to send a query to. The port exists because
/// the module may not name a host type, not because a module boundary is being crossed.
/// </para>
/// <para>
/// Both predicates are load-bearing. <c>asp_net_users</c> is RLS-exempt, so the org comparison is the
/// only thing standing between a cohort rule and a user from another tenant; matching on id alone
/// would authorize exactly that.
/// </para>
/// </summary>
internal sealed class OrgMembershipAdapter(AppDbContext db) : IOrgMembership
{
    public Task<bool> IsUserInOrgAsync(Guid userId, Guid orgId, CancellationToken ct = default) =>
        db.Set<AppUser>().AnyAsync(user => user.Id == userId && user.OrgId == orgId, ct);
}
