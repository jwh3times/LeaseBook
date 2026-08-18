using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Audit;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-02 (P52/P56): actor attribution + the per-entry audit trail. A post made by an authenticated
/// user stamps <c>journal_entries.created_by</c> and <c>audit_events.actor_user_id</c>; the audit-trail
/// read resolves the actor's name/email (org-filtered identity lookup, M3-E6) and covers the reversal;
/// and another org cannot read the trail. The actor is set in-process here (as the middleware does
/// from the claim); the over-HTTP path is WP-03.
/// <para>
/// ADR-039 changed what a system write leaves behind. It used to stamp a null and rely on the call
/// site to remember which process acted; it now records <c>actor_kind</c> = <c>system</c> and the
/// process name, so these tests read the persisted row rather than the in-memory
/// <see cref="Actor"/> — the whole point of the ADR is that the two can no longer disagree.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ActorAuditTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";
    private static readonly DateOnly Feb1 = new(2026, 2, 1);
    private static readonly DateOnly Feb3 = new(2026, 2, 3);

    [Fact]
    public async Task A_post_by_a_user_stamps_created_by_and_the_audit_actor()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var (userId, _) = await CreateUserAsync(orgId, "Renée Calloway", ct);
        var tenantId = await SetupTenantAsync(orgId, ct);

        var posted = await AsActorAsync(orgId, userId,
            (_, s, c) => s.Send(new AddCharge(tenantId, 1450m, Feb1, "rent", null, Key()), c), ct);

        var (createdBy, auditActor) = await AsActorAsync(orgId, null, async (sp, _, c) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var cb = await db.Set<JournalEntry>()
                .Where(e => e.Id == posted.EntryId)
                .Select(e => new { e.CreatedBy, e.ActorKind, e.ActorProcess }).SingleAsync(c);
            var actor = await db.AuditEvents
                .Where(a => a.EntityType == "journal_entries" && a.EntityId == posted.EntryId)
                .Select(a => new { a.ActorUserId, a.ActorKind, a.ActorProcess }).FirstAsync(c);
            return (cb, actor);
        }, ct);

        createdBy.CreatedBy.ShouldBe(userId);
        createdBy.ActorKind.ShouldBe("user");
        createdBy.ActorProcess.ShouldBeNull("a user actor names no process — the check constraint agrees");
        auditActor.ActorUserId.ShouldBe(userId);
        auditActor.ActorKind.ShouldBe("user");
        auditActor.ActorProcess.ShouldBeNull();
    }

    [Fact]
    public async Task The_audit_trail_resolves_the_actor_and_covers_the_reversal_newest_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var (userId, email) = await CreateUserAsync(orgId, "Renée Calloway", ct);
        var tenantId = await SetupTenantAsync(orgId, ct);

        var charge = await AsActorAsync(orgId, userId,
            (_, s, c) => s.Send(new AddCharge(tenantId, 1450m, Feb1, "rent", null, Key()), c), ct);
        await AsActorAsync(orgId, userId,
            (_, s, c) => s.Send(new VoidEntry(charge.EntryId, "entered in error", Feb3, Key()), c), ct);

        var trail = await AsActorAsync(orgId, null,
            (sp, _, c) => sp.GetRequiredService<EntryAuditReader>().GetAsync(charge.EntryId, c), ct);

        trail.Rows.Count.ShouldBe(2); // the original insert + the reversal insert
        trail.Rows.ShouldAllBe(r => r.Action == "insert");
        trail.Rows.ShouldAllBe(r => r.ActorName == "Renée Calloway" && r.ActorEmail == email);
        trail.Rows[0].OccurredAt.ShouldBeGreaterThanOrEqualTo(trail.Rows[1].OccurredAt); // newest first
    }

    /// <summary>
    /// The ADR-039 case. Before it, this test asserted only that a system write stamped null without
    /// throwing — which is precisely the reading an auditor could not act on, since a null is also
    /// what a forgotten actor leaves. The row now names the process, on both the journal and the
    /// audit trail.
    /// </summary>
    [Fact]
    public async Task A_system_write_records_which_process_acted()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var tenantId = await SetupTenantAsync(orgId, ct);

        // The seeder/job path: a named system process, not an absent actor.
        var posted = await AsActorAsync(orgId, null,
            (_, s, c) => s.Send(new AddCharge(tenantId, 1450m, Feb1, "rent", null, Key()), c), ct);

        var (entry, audit) = await AsActorAsync(orgId, null, async (sp, _, c) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var e = await db.Set<JournalEntry>()
                .Where(j => j.Id == posted.EntryId)
                .Select(j => new { j.CreatedBy, j.ActorKind, j.ActorProcess }).SingleAsync(c);
            var a = await db.AuditEvents
                .Where(x => x.EntityType == "journal_entries" && x.EntityId == posted.EntryId)
                .Select(x => new { x.ActorUserId, x.ActorKind, x.ActorProcess }).FirstAsync(c);
            return (e, a);
        }, ct);

        entry.CreatedBy.ShouldBeNull("no human is accountable for a system write");
        entry.ActorKind.ShouldBe("system");
        entry.ActorProcess.ShouldBe("test-harness");

        audit.ActorUserId.ShouldBeNull();
        audit.ActorKind.ShouldBe("system");
        audit.ActorProcess.ShouldBe("test-harness");
    }

    /// <summary>
    /// Fail closed. ADR-039's rule only holds if an absent actor is refused rather than written as a
    /// null, so this drives the one state the executor cannot produce: organization context set, no
    /// unit of work, therefore no declared actor.
    /// </summary>
    [Fact]
    public async Task A_write_with_no_declared_actor_is_refused_before_it_reaches_the_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // Organization context by hand, deliberately without OrgScopedExecutor — which is the only
        // thing that declares an actor. This is the shape a background job written the wrong way has.
        sp.GetRequiredService<TenantContext>().OrgId = orgId;
        var db = sp.GetRequiredService<AppDbContext>();
        db.Set<Owner>().Add(new Owner { Name = "Unattributed" });

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync(ct));
        ex.Message.ShouldContain("no declared actor");
    }

    /// <summary>
    /// The same refusal on the money path, checked beside the organization context so it fires before
    /// any posting work rather than after a partial read.
    /// </summary>
    [Fact]
    public async Task Posting_with_no_declared_actor_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<TenantContext>().OrgId = orgId;

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => sp.GetRequiredService<IPostingService>().PostAsync(
                new PostEntryRequest(Feb1, "RentCharged", null, null, null, []), ct));
        ex.Message.ShouldContain("declared actor");
    }

    // The unit of work's actor parameter is what decides attribution — not an ambient value a caller
    // set beforehand. Before the parameter existed, presetting ActorContext by hand was the ONLY way
    // to attribute a job or seeder write, and ScenarioSeeder did exactly that from inside the work.
    // Money-adjacent: this is journal_entries.created_by on a real posting.
    [Fact]
    public async Task The_units_actor_decides_created_by_and_a_preset_context_does_not_leak_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var (userId, _) = await CreateUserAsync(orgId, "Renée Calloway", ct);
        var tenantId = await SetupTenantAsync(orgId, ct);

        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var executor = sp.GetRequiredService<OrgScopedExecutor>();
        var sender = sp.GetRequiredService<ISender>();

        // A caller sets the context by hand, the old way, and then declares a system unit of work.
        sp.GetRequiredService<ActorContext>().Actor = Actor.User(userId);

        var systemEntry = Guid.Empty;
        await executor.RunAsSystemAsync(orgId, "nightly-job", async () =>
            systemEntry = (await sender.Send(new AddCharge(tenantId, 1450m, Feb1, "rent", null, Key()), ct)).EntryId, ct);

        var userEntry = Guid.Empty;
        await executor.RunAsync(orgId, Actor.User(userId), async () =>
            userEntry = (await sender.Send(new AddCharge(tenantId, 1450m, Feb3, "rent", null, Key()), ct)).EntryId, ct);

        var stamps = await AsActorAsync(orgId, null, (s, _, c) =>
            s.GetRequiredService<AppDbContext>().Set<JournalEntry>()
                .Where(e => e.Id == systemEntry || e.Id == userEntry)
                .ToDictionaryAsync(e => e.Id, e => new { e.CreatedBy, e.ActorKind, e.ActorProcess }, c), ct);

        stamps[systemEntry].CreatedBy.ShouldBeNull(
            "the unit of work declared a system actor, so a value set before it must not survive into the posting");
        stamps[systemEntry].ActorKind.ShouldBe("system");
        stamps[systemEntry].ActorProcess.ShouldBe("nightly-job");
        stamps[userEntry].CreatedBy.ShouldBe(userId);
        stamps[userEntry].ActorKind.ShouldBe("user");
    }

    [Fact]
    public async Task The_audit_trail_is_isolated_across_orgs()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = await NewOrgAsync(ct);
        var (userA, _) = await CreateUserAsync(orgA, "User A", ct);
        var tenantA = await SetupTenantAsync(orgA, ct);
        var charge = await AsActorAsync(orgA, userA,
            (_, s, c) => s.Send(new AddCharge(tenantA, 1450m, Feb1, "rent", null, Key()), c), ct);

        var orgB = await NewOrgAsync(ct);

        // Org B reads org A's entry id: RLS hides the entry, so the trail is empty — no row, no actor
        // name to leak (the identity lookup is org-filtered too, M3-E6).
        var trail = await AsActorAsync(orgB, null,
            (sp, _, c) => sp.GetRequiredService<EntryAuditReader>().GetAsync(charge.EntryId, c), ct);
        trail.Rows.ShouldBeEmpty();
    }

    private static string Key() => UuidV7.NewId().ToString();

    private async Task<Guid> SetupTenantAsync(Guid orgId, CancellationToken ct)
    {
        Guid tenantId = default;
        await AsActorAsync(orgId, null, async (sp, s, c) =>
        {
            // Provision the chart of accounts (the five singletons) so charges can post.
            await sp.GetRequiredService<IChartOfAccounts>().ProvisionAsync([], c);
            var ownerId = await s.Send(new CreateOwner("Owner", null, null, null, 800, 0m), c);
            var propertyId = await s.Send(new CreateProperty(ownerId, "412 Oakmont Ave", "Asheville", "NC", "28801", null), c);
            var unitId = await s.Send(new CreateUnit(propertyId, "#2B", 1450m, "available"), c);
            tenantId = await s.Send(new CreateTenant("Jasmine Carter", null, null, "current"), c);
            await s.Send(new CreateLease(tenantId, unitId, new DateOnly(2025, 6, 1), new DateOnly(2026, 5, 31), 1450m, 1450m, "active"), c);
            return 0;
        }, ct);
        return tenantId;
    }

    private async Task<Guid> NewOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Actor Audit Org {orgId:N}" });
        await migratorDb.SaveChangesAsync(ct);
        return orgId;
    }

    private async Task<(Guid Id, string Email)> CreateUserAsync(Guid orgId, string displayName, CancellationToken ct)
    {
        // Identity users are global (no org RLS) → email must be unique across orgs/tests.
        var email = $"user-{orgId:N}@example.com";
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            Id = UuidV7.NewId(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            OrgId = orgId,
            DisplayName = displayName,
        };
        (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
        return (user.Id, email);
    }

    private async Task<T> AsActorAsync<T>(
        Guid orgId, Guid? actorUserId, Func<IServiceProvider, ISender, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // The executor owns ActorContext for the length of the unit of work, exactly as the request
        // path does. Setting it by hand before the call would simply be overwritten.
        var actor = actorUserId is { } uid ? Actor.User(uid) : Actor.System("test-harness");

        var executor = sp.GetRequiredService<OrgScopedExecutor>();
        T result = default!;
        await executor.RunAsync(orgId, actor, async () => result = await work(sp, sp.GetRequiredService<ISender>(), ct), ct);
        return result;
    }
}
