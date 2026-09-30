using System.Security.Claims;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Auth;

namespace LeaseBook.Web.Tenancy;

/// <summary>
/// Turns an authenticated principal's roles into the <see cref="Persona"/> its request runs under at
/// the database (#314, ADR-048). Resolved by <see cref="OrgContextMiddleware"/> before the request's
/// transaction opens; the executor writes it to <c>app.persona</c>.
/// <para>
/// Exclusive by construction, matching the portal authorization policies. A principal is
/// <see cref="Persona.Staff"/> only when every role it holds is PMAdmin or PMStaff, and a portal
/// persona only when the portal role is its <i>only</i> role. Every other combination — a portal role
/// alongside anything, a role outside <see cref="Roles.All"/>, or no role at all — is
/// <see cref="Persona.None"/>, which the database gate admits nowhere. Mixing is refused rather than
/// resolved to the wider side on purpose: an account that is both an owner and a staff member is an
/// administrative mistake, and failing closed makes it visible instead of quietly org-wide.
/// </para>
/// </summary>
public static class PersonaResolver
{
    public static Persona Resolve(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated != true)
        {
            return Persona.None;
        }

        var roles = principal.Identities
            .SelectMany(identity => identity.FindAll(identity.RoleClaimType))
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);

        if (roles.Count == 0 || roles.Any(role => !Roles.All.Contains(role, StringComparer.Ordinal)))
        {
            return Persona.None;
        }

        if (roles.All(role => role is Roles.PMAdmin or Roles.PMStaff))
        {
            return Persona.Staff;
        }

        if (roles.Count == 1)
        {
            return roles.Single() switch
            {
                Roles.Tenant => Persona.Tenant,
                Roles.Owner => Persona.Owner,
                _ => Persona.None,
            };
        }

        return Persona.None;
    }
}
