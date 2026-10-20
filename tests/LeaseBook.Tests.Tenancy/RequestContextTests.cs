using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Tenancy.Support;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LeaseBook.Tests.Tenancy;

/// <summary>
/// The request-scoped mirrors of the database context: <see cref="OrgContext"/> and
/// <see cref="ActorContext"/>, which the executor writes and everything else reads, and the
/// <c>NullOrgContext</c> an <see cref="AppDbContext"/> falls back to when it is built outside DI.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RequestContextTests(PostgresFixture fixture)
{
    [Fact]
    public void A_new_org_context_has_no_organization()
    {
        IOrgContext context = new OrgContext();
        context.OrgId.ShouldBeNull();
    }

    [Fact]
    public void A_new_actor_context_has_no_actor_and_no_user()
    {
        IActorContext context = new ActorContext();

        context.Actor.ShouldBeNull();
        context.UserId.ShouldBeNull();
    }

    [Fact]
    public void The_actor_contexts_user_is_the_human_actors_and_none_for_a_system_actor()
    {
        var id = Guid.NewGuid();
        var context = new ActorContext { Actor = Actor.User(id) };

        ((IActorContext)context).UserId.ShouldBe(id);

        context.Actor = Actor.System("seed:demo");
        ((IActorContext)context).UserId.ShouldBeNull();
    }

    /// <summary>
    /// An <see cref="AppDbContext"/> built with no organization context reads under one that has no
    /// organization, so its query filter matches no org-scoped row — even where the database, as
    /// here, would return them.
    /// </summary>
    [Fact]
    public async Task A_context_built_outside_DI_has_no_organization_so_its_query_filter_matches_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await TenancyDb.NewOrgAsync(fixture, ct);
        await TenancyDb.PlantEventsAsync(fixture, orgId, 2, ct);
        await using var db = fixture.CreateContext(fixture.AppConnectionString);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(db, orgId, ct);

        var unfiltered = await db.AuditEvents.IgnoreQueryFilters().CountAsync(ct);
        var filtered = await db.AuditEvents.CountAsync(ct);

        unfiltered.ShouldBe(2, "the database admits the organization's rows");
        filtered.ShouldBe(0, "the fallback context names no organization, so the filter admits none");
    }

    [Fact]
    public async Task A_context_built_with_an_organization_context_filters_to_that_organization()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await TenancyDb.NewOrgAsync(fixture, ct);
        await TenancyDb.PlantEventsAsync(fixture, orgId, 2, ct);
        await using var db = fixture.CreateContext(fixture.AppConnectionString, new OrgContext { OrgId = orgId },
            new ActorContext());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(db, orgId, ct);

        (await db.AuditEvents.CountAsync(ct)).ShouldBe(2);
    }
}
