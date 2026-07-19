using LeaseBook.Modules.Accounting.Contracts;
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
using LeaseBook.Web.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-8: the compliance pack's audit-log extract. <see cref="AuditExtractReader"/> reads
/// <c>audit_events</c> for a period, keeps only money-touching <c>entity_type</c>s (journal/posting,
/// reconciliation, bank-line, period, statement-linking, sign-off) and drops config/directory/identity
/// noise, resolving each actor via the org-filtered identity lookup (M3-E6). PMAdmin gating is enforced
/// at the endpoint, not here.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class AuditExtractTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";
    private static readonly DateOnly Feb1 = new(2026, 2, 1);

    [Fact]
    public async Task Extract_returns_money_touching_events_in_period_and_excludes_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var (userId, email) = await CreateUserAsync(orgId, "Renée Calloway", ct);
        var tenantId = await SetupTenantAsync(orgId, ct); // emits non-money directory + chart audits

        // A posting the actor makes → journal_entries + journal_lines audits (money-touching).
        await AsActorAsync(orgId, userId,
            (_, s, c) => s.Send(new AddCharge(tenantId, 1450m, Feb1, "rent", null, Key()), c), ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var (extract, rawCount) = await AsActorAsync(orgId, null, async (sp, _, c) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var reader = new AuditExtractReader(db, sp.GetRequiredService<ITenantContext>());
            var result = await reader.GetAsync(today.AddDays(-1), today.AddDays(1), c);
            var total = await db.AuditEvents.CountAsync(c);
            return (result, total);
        }, ct);

        extract.Rows.ShouldNotBeEmpty();
        extract.Rows.ShouldAllBe(r => AuditExtractReader.MoneyTouchingEntityTypes.Contains(r.EntityType));
        extract.Rows.ShouldContain(r => r.EntityType == "journal_entries");
        extract.Rows.ShouldContain(r => r.ActorName == "Renée Calloway" && r.ActorEmail == email);

        // Setup created directory + chart rows (owners/properties/units/tenants/leases/accounts), all of
        // which are audited but non-money — so the extract is strictly smaller than the raw audit log.
        rawCount.ShouldBeGreaterThan(extract.Rows.Count);
    }

    [Fact]
    public async Task Extract_is_bounded_to_the_period()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var tenantId = await SetupTenantAsync(orgId, ct);
        await AsActorAsync(orgId, null,
            (_, s, c) => s.Send(new AddCharge(tenantId, 1450m, Feb1, "rent", null, Key()), c), ct);

        // Every audit row occurred "now"; a 2020 window contains none of them.
        var extract = await AsActorAsync(orgId, null, (sp, _, c) =>
            new AuditExtractReader(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<ITenantContext>())
                .GetAsync(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), c), ct);

        extract.Rows.ShouldBeEmpty();
    }

    // WP-8: pin the audit universe so a new money-touching table can't silently drop out of the extract.
    // Every entity_type the demo seed emits must be classified as either money-touching (the extract's
    // allowlist) or explicitly non-money below; an unclassified one fails the test until it's triaged.
    [Fact]
    public async Task Money_touching_allowlist_has_no_drift_against_the_demo_seed()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var emitted = await AsActorAsync(DemoSeeder.DemoOrgId, null, (sp, _, c) =>
            sp.GetRequiredService<AppDbContext>().AuditEvents.AsNoTracking()
                .Select(a => a.EntityType).Distinct().ToListAsync(c), ct);

        var known = AuditExtractReader.MoneyTouchingEntityTypes.Concat(KnownNonMoneyEntityTypes).ToHashSet();
        var unclassified = emitted.Where(e => !known.Contains(e)).ToList();
        unclassified.ShouldBeEmpty(
            $"unclassified audit entity_types — add each to the money allowlist or the non-money set: {string.Join(", ", unclassified)}");
    }

    // Non-money audit entity_types the demo seed emits: directory/config/provisioning writes, not money
    // movement. Paired with AuditExtractReader.MoneyTouchingEntityTypes to pin the audit universe.
    // Coverage note: the demo seed does not finalize reconciliations or run an import, so the
    // cutover-only money types (bank_reconciliations, accounting_periods, statement_matches,
    // statement_imports, migration-signed-off) are covered by the allowlist per the design-gate source
    // verification but are not exercised here — extending this guard to the cutover seed is a follow-up.
    // "statement_deliveries" (WP-5): a delivery-record state (queued/sent/failed) for an
    // already-computed statement, not a money movement — surfaced when Security/DeliverTelemetryTests
    // exercised the real deliver endpoint against the demo org in the same test collection.
    private static readonly string[] KnownNonMoneyEntityTypes =
    [
        "accounts", "bank_accounts", "lease_lite", "org-provisioned", "org_settings",
        "owners", "properties", "statement_deliveries", "tenants", "units",
    ];

    private static string Key() => UuidV7.NewId().ToString();

    private async Task<Guid> SetupTenantAsync(Guid orgId, CancellationToken ct)
    {
        Guid tenantId = default;
        await AsActorAsync(orgId, null, async (sp, s, c) =>
        {
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
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Audit Extract Org {orgId:N}" });
        await migratorDb.SaveChangesAsync(ct);
        return orgId;
    }

    private async Task<(Guid Id, string Email)> CreateUserAsync(Guid orgId, string displayName, CancellationToken ct)
    {
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
