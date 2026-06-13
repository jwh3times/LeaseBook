using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
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
/// a system write (no actor) stamps null without throwing; and another org cannot read the trail.
/// The actor is set in-process here (as the middleware does from the claim); the over-HTTP path is WP-03.
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
            var cb = await db.Set<JournalEntry>().Where(e => e.Id == posted.EntryId).Select(e => e.CreatedBy).SingleAsync(c);
            var actor = await db.AuditEvents
                .Where(a => a.EntityType == "journal_entries" && a.EntityId == posted.EntryId)
                .Select(a => a.ActorUserId).FirstAsync(c);
            return (cb, actor);
        }, ct);

        createdBy.ShouldBe(userId);
        auditActor.ShouldBe(userId);
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

    [Fact]
    public async Task A_system_write_with_no_actor_stamps_null_without_throwing()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var tenantId = await SetupTenantAsync(orgId, ct);

        // No actor set on the scope (the seeder/job path).
        var posted = await AsActorAsync(orgId, null,
            (_, s, c) => s.Send(new AddCharge(tenantId, 1450m, Feb1, "rent", null, Key()), c), ct);

        var createdBy = await AsActorAsync(orgId, null, (sp, _, c) =>
            sp.GetRequiredService<AppDbContext>().Set<JournalEntry>()
                .Where(e => e.Id == posted.EntryId).Select(e => e.CreatedBy).SingleAsync(c), ct);
        createdBy.ShouldBeNull();
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
            var unitId = await s.Send(new CreateUnit(propertyId, "#2B", 1450m, "occupied"), c);
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
        if (actorUserId is { } uid)
        {
            sp.GetRequiredService<ActorContext>().UserId = uid;
        }

        var executor = sp.GetRequiredService<OrgScopedExecutor>();
        T result = default!;
        await executor.RunAsync(orgId, async () => result = await work(sp, sp.GetRequiredService<ISender>(), ct), ct);
        return result;
    }
}
