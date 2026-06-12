using System.Security.Claims;
using LeaseBook.Web.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace LeaseBook.Web.Auth;

/// <summary>
/// Adds the <c>org_id</c> claim at sign-in so the cookie carries the caller's org. This is the seam
/// the tenancy middleware reads to establish <c>app.org_id</c> for the request (§C.4 / WP-05). Role
/// claims are added by the base factory.
/// </summary>
public sealed class AppUserClaimsPrincipalFactory(
    UserManager<AppUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole<Guid>>(userManager, roleManager, optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(OrgContextMiddleware.OrgIdClaim, user.OrgId.ToString()));
        return identity;
    }
}
