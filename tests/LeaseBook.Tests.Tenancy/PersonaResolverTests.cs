using System.Security.Claims;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Portal;
using LeaseBook.Web.Tenancy;
using Shouldly;

namespace LeaseBook.Tests.Tenancy;

/// <summary>
/// Roles to database persona (#314, ADR-048). Exclusive in both directions: org-wide only for a
/// principal holding nothing but staff roles, a portal persona only for a principal holding nothing
/// but that portal role, and <see cref="Persona.None"/> — which the gate admits nowhere — for
/// everything else.
/// <para>
/// Moved here from <c>LeaseBook.Tests.Web</c> so that mutation testing, which reruns only this
/// project, measures the resolver against them.
/// </para>
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
    [InlineData(Persona.None, Roles.Tenant, "Auditor")]
    [InlineData(Persona.None, "pmstaff")]
    [InlineData(Persona.None, "tenant")]
    public void Roles_resolve_to_exactly_one_persona(Persona expected, params string[] roles)
    {
        PersonaResolver.Resolve(Principal(roles)).ShouldBe(expected);
    }

    /// <summary>A role held twice is one role: the set, not the claim count, decides.</summary>
    [Theory]
    [InlineData(Persona.Tenant, Roles.Tenant, Roles.Tenant)]
    [InlineData(Persona.Owner, Roles.Owner, Roles.Owner)]
    [InlineData(Persona.Staff, Roles.PMStaff, Roles.PMStaff)]
    public void A_role_claimed_twice_counts_once(Persona expected, params string[] roles)
    {
        PersonaResolver.Resolve(Principal(roles)).ShouldBe(expected);
    }

    [Fact]
    public void An_unauthenticated_principal_is_none_whatever_its_roles()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.PMAdmin)]));
        PersonaResolver.Resolve(anonymous).ShouldBe(Persona.None);
        anonymous.Identity!.IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public void A_principal_with_no_identity_is_none()
    {
        PersonaResolver.Resolve(new ClaimsPrincipal()).ShouldBe(Persona.None);
    }

    [Fact]
    public void A_missing_principal_is_refused()
    {
        Should.Throw<ArgumentNullException>(() => PersonaResolver.Resolve(null!)).ParamName.ShouldBe("principal");
    }

    /// <summary>
    /// The roles are read under each identity's own role claim type, as <c>IsInRole</c> reads them,
    /// so an identity whose roles travel under another claim type resolves by those.
    /// </summary>
    [Fact]
    public void Roles_are_read_under_the_identitys_own_role_claim_type()
    {
        const string roleType = "urn:leasebook:test-role";
        var identity = new ClaimsIdentity(
            [new Claim(roleType, Roles.Tenant), new Claim(ClaimTypes.Role, Roles.PMAdmin)],
            authenticationType: "test",
            nameType: ClaimTypes.Name,
            roleType: roleType);

        PersonaResolver.Resolve(new ClaimsPrincipal(identity)).ShouldBe(Persona.Tenant);
    }

    /// <summary>Every identity of the principal contributes its roles, so a mix across two is still a mix.</summary>
    [Theory]
    [InlineData(Persona.Staff, Roles.PMAdmin, Roles.PMStaff)]
    [InlineData(Persona.None, Roles.PMStaff, Roles.Owner)]
    [InlineData(Persona.None, Roles.Tenant, Roles.Owner)]
    [InlineData(Persona.Tenant, Roles.Tenant, Roles.Tenant)]
    public void Roles_are_gathered_across_every_identity(Persona expected, string first, string second)
    {
        var principal = new ClaimsPrincipal(
        [
            new ClaimsIdentity([new Claim(ClaimTypes.Role, first)], "test"),
            new ClaimsIdentity([new Claim(ClaimTypes.Role, second)], "second"),
        ]);

        PersonaResolver.Resolve(principal).ShouldBe(expected);
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
