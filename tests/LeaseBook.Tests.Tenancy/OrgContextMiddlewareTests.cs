using System.Security.Claims;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Tenancy.Support;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Tenancy;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace LeaseBook.Tests.Tenancy;

/// <summary>
/// <see cref="OrgContextMiddleware"/>, the HTTP half of the tenancy pattern: an authenticated request
/// with an organization runs the rest of the pipeline inside one <see cref="OrgScopedExecutor"/> unit
/// of work, under the persona its roles resolve to; any other request runs with no transaction and
/// no context, which row-level security answers with nothing. Driven directly, over a real session,
/// with a hand-built <see cref="HttpContext"/> and no host.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OrgContextMiddlewareTests(PostgresFixture fixture)
{
    private static readonly Guid OrgId = Guid.Parse("0199f0aa-2222-7333-8444-955566667777");
    private static readonly Guid UserId = Guid.Parse("0199f0aa-1111-7222-8333-944455556666");

    [Fact]
    public async Task An_authenticated_staff_request_runs_the_pipeline_in_one_unit_of_work_for_its_org()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);

        var seen = await InvokeAsync(session, Principal(OrgId.ToString(), UserId.ToString(), Roles.PMStaff), ct);

        seen.Calls.ShouldBe(1);
        seen.InTransaction.ShouldBeTrue();
        seen.Session!.Org.ShouldBe(OrgId.ToString());
        seen.Session.Persona.ShouldBe("staff");
        seen.Session.User.ShouldBe(UserId.ToString());
        seen.Org.ShouldBe(OrgId);
        seen.Actor!.Reference.ShouldBe($"user:{UserId}");
        session.Transactions.Events.ShouldBe(["begin", "commit"]);
        session.Org.OrgId.ShouldBeNull("the context dies with the unit of work");
    }

    [Theory]
    [InlineData(Persona.Tenant, "tenant", Roles.Tenant)]
    [InlineData(Persona.Owner, "owner", Roles.Owner)]
    [InlineData(Persona.Staff, "staff", Roles.PMAdmin)]
    [InlineData(Persona.Staff, "staff", Roles.PMAdmin, Roles.PMStaff)]
    [InlineData(Persona.None, "none", Roles.PMStaff, Roles.Owner)]
    [InlineData(Persona.None, "none")]
    public async Task A_user_request_runs_under_the_persona_its_roles_resolve_to(
        Persona persona, string personaText, params string[] roles)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var principal = Principal(OrgId.ToString(), UserId.ToString(), roles);
        PersonaResolver.Resolve(principal).ShouldBe(persona);

        var seen = await InvokeAsync(session, principal, ct);

        seen.Session!.Persona.ShouldBe(personaText);
        seen.Session.User.ShouldBe(UserId.ToString());
        seen.Actor!.Reference.ShouldBe($"user:{UserId}");
    }

    /// <summary>
    /// An authenticated principal whose user id is missing, empty or unreadable is named as such,
    /// never left anonymous, and keeps the persona of its roles where that is organization-wide.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-user-id")]
    public async Task A_principal_without_a_usable_user_id_runs_as_a_named_system_actor(string? userClaim)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);

        var seen = await InvokeAsync(session, Principal(OrgId.ToString(), userClaim, Roles.PMStaff), ct);

        seen.InTransaction.ShouldBeTrue();
        seen.Actor!.Reference.ShouldBe("system:principal-without-user-id");
        seen.Session!.User.ShouldBe("");
        seen.Session.Persona.ShouldBe("staff");
        seen.Session.Org.ShouldBe(OrgId.ToString());
    }

    /// <summary>
    /// A portal persona is granted rows through its user's link, so a principal with no usable user
    /// id has nothing to be granted through. It runs as none rather than as the portal persona the
    /// executor would refuse — and never as the system persona its actor names.
    /// </summary>
    [Theory]
    [InlineData(null, Roles.Tenant)]
    [InlineData(null, Roles.Owner)]
    [InlineData("00000000-0000-0000-0000-000000000000", Roles.Owner)]
    [InlineData("not-a-user-id", Roles.Tenant)]
    public async Task A_portal_principal_without_a_usable_user_id_runs_as_none(string? userClaim, string role)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);

        var seen = await InvokeAsync(session, Principal(OrgId.ToString(), userClaim, role), ct);

        seen.Calls.ShouldBe(1);
        seen.Actor!.Reference.ShouldBe("system:principal-without-user-id");
        seen.Session!.Persona.ShouldBe("none");
        seen.Session.User.ShouldBe("");
    }

    [Fact]
    public async Task An_unauthenticated_request_runs_with_no_transaction_and_no_context()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        // The org and user claims are there; the identity is not authenticated.
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(OrgContextMiddleware.OrgIdClaim, OrgId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
            new Claim(ClaimTypes.Role, Roles.PMAdmin),
        ]));

        await ShouldRunWithoutContextAsync(session, anonymous, ct);
    }

    [Fact]
    public async Task A_request_with_no_identity_runs_with_no_transaction_and_no_context()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);

        await ShouldRunWithoutContextAsync(session, new ClaimsPrincipal(), ct);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-org-id")]
    public async Task An_authenticated_request_with_no_readable_org_runs_with_no_transaction_and_no_context(
        string? orgClaim)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);

        await ShouldRunWithoutContextAsync(session, Principal(orgClaim, UserId.ToString(), Roles.PMStaff), ct);
    }

    [Fact]
    public void The_org_claim_is_org_id()
    {
        OrgContextMiddleware.OrgIdClaim.ShouldBe("org_id");
    }

    [Fact]
    public async Task A_pipeline_that_throws_is_rolled_back_and_the_exception_reaches_the_caller()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await TenancyDb.NewOrgAsync(fixture, ct);
        await using var session = await Session.OpenAsync(fixture, ct);
        var failure = new InvalidOperationException("the endpoint failed");
        var middleware = new OrgContextMiddleware(async _ =>
        {
            await session.InsertEventAsync(orgId, ct);
            throw failure;
        });
        var context = new DefaultHttpContext { User = Principal(orgId.ToString(), UserId.ToString(), Roles.PMStaff) };

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, session.Executor));

        thrown.ShouldBeSameAs(failure);
        session.Transactions.Events.ShouldBe(["begin", "rollback"]);
        (await TenancyDb.CommittedEventsAsync(fixture, orgId, ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_request_that_completes_commits_what_the_pipeline_wrote()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await TenancyDb.NewOrgAsync(fixture, ct);
        await using var session = await Session.OpenAsync(fixture, ct);
        var middleware = new OrgContextMiddleware(_ => session.InsertEventAsync(orgId, ct));
        var context = new DefaultHttpContext { User = Principal(orgId.ToString(), UserId.ToString(), Roles.PMStaff) };

        await middleware.InvokeAsync(context, session.Executor);

        (await TenancyDb.CommittedEventsAsync(fixture, orgId, ct)).ShouldBe(1);
    }

    private static async Task ShouldRunWithoutContextAsync(Session session, ClaimsPrincipal principal, CancellationToken ct)
    {
        var seen = await InvokeAsync(session, principal, ct);

        seen.Calls.ShouldBe(1);
        seen.InTransaction.ShouldBeFalse();
        seen.Org.ShouldBeNull();
        seen.Actor.ShouldBeNull();
        seen.Session!.Org.ShouldBeNull();
        seen.Session.Persona.ShouldBeNull();
        seen.Session.User.ShouldBeNull();
        session.Transactions.Events.ShouldBeEmpty();
    }

    private static async Task<Seen> InvokeAsync(Session session, ClaimsPrincipal principal, CancellationToken ct)
    {
        var seen = new Seen();
        var middleware = new OrgContextMiddleware(async context =>
        {
            seen.Calls++;
            context.User.ShouldBeSameAs(principal);
            seen.InTransaction = session.Db.Database.CurrentTransaction is not null;
            seen.Org = session.Org.OrgId;
            seen.Actor = session.Actor.Actor;
            seen.Session = await session.ReadAsync(ct);
        });

        await middleware.InvokeAsync(new DefaultHttpContext { User = principal }, session.Executor);
        return seen;
    }

    /// <summary>An authenticated principal with the given org and user claims, and roles.</summary>
    private static ClaimsPrincipal Principal(string? orgClaim, string? userClaim, params string[] roles)
    {
        var claims = new List<Claim>();
        if (orgClaim is not null)
        {
            claims.Add(new Claim(OrgContextMiddleware.OrgIdClaim, orgClaim));
        }

        if (userClaim is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userClaim));
        }

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }

    private sealed class Seen
    {
        public int Calls { get; set; }

        public bool InTransaction { get; set; }

        public Guid? Org { get; set; }

        public Actor? Actor { get; set; }

        public SessionContext? Session { get; set; }
    }
}
