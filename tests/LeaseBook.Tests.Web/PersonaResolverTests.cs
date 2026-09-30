using System.Security.Claims;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Portal;
using LeaseBook.Web.Tenancy;
using Shouldly;

namespace LeaseBook.Tests.Web;

/// <summary>
/// Roles to database persona (#314, ADR-048). Exclusive in both directions: org-wide only for a
/// principal holding nothing but staff roles, a portal persona only for a principal holding nothing
/// but that portal role, and <see cref="Persona.None"/> — which the gate admits nowhere — for
/// everything else.
/// </summary>
public sealed class PersonaResolverTests
{
    [Theory]
    [InlineData(Persona.Staff, Roles.PMAdmin)]
    [InlineData(Persona.Staff, Roles.PMStaff)]
    [InlineData(Persona.Staff, Roles.PMAdmin, Roles.PMStaff)]
    [InlineData(Persona.Tenant, Roles.Tenant)]
    [InlineData(Persona.Owner, Roles.Owner)]
    [InlineData(Persona.None)]
    [InlineData(Persona.None, Roles.PMStaff, Roles.Owner)]
    [InlineData(Persona.None, Roles.PMAdmin, Roles.Tenant)]
    [InlineData(Persona.None, Roles.Owner, Roles.Tenant)]
    [InlineData(Persona.None, Roles.PMAdmin, Roles.PMStaff, Roles.Owner, Roles.Tenant)]
    [InlineData(Persona.None, "Auditor")]
    [InlineData(Persona.None, Roles.PMStaff, "Auditor")]
    [InlineData(Persona.None, "pmstaff")]
    public void Roles_resolve_to_exactly_one_persona(Persona expected, params string[] roles)
    {
        PersonaResolver.Resolve(Principal(roles)).ShouldBe(expected);
    }

    [Fact]
    public void An_unauthenticated_principal_is_none()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.PMAdmin)]));
        PersonaResolver.Resolve(anonymous).ShouldBe(Persona.None);
    }

    /// <summary>
    /// The owner persona at the database is the owner persona the portal admits — the same principal
    /// never passes one check and fails the other.
    /// </summary>
    [Theory]
    [InlineData(Roles.Owner)]
    [InlineData(Roles.Owner, Roles.PMStaff)]
    [InlineData(Roles.Owner, Roles.Tenant)]
    [InlineData(Roles.PMAdmin)]
    public void The_database_owner_persona_matches_the_portal_owner_persona(params string[] roles)
    {
        var principal = Principal(roles);
        (PersonaResolver.Resolve(principal) == Persona.Owner).ShouldBe(OwnerPersona.Admits(principal));
    }

    private static ClaimsPrincipal Principal(string[] roles) =>
        new(new ClaimsIdentity(
            roles.Select(role => new Claim(ClaimTypes.Role, role)).Prepend(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())),
            authenticationType: "test"));
}
