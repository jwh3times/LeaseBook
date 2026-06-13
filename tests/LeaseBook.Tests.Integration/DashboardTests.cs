using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Dashboard;
using LeaseBook.Web.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-05: the host-composed dashboard. Against the seeded demo org the KPIs reconcile to the M1 golden
/// figures (trustTotal 483,620.69; ownersPayable is the P41 computed value, not the prototype's noise),
/// the hero is named with the relabeled roll-up and ties to the bank total, and a second org sees none of
/// it. <c>collectedMtd</c> is pinned with a fixed clock (June 2026).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class DashboardTests(PostgresFixture fixture)
{
    // P41 canonical figure: Σ max(0, operating) over the 8 non-system owners (o4's −420 → 0; the
    // AggregateOwners roll-up excluded). Replaces the prototype's 132,447.00 (m1 §C.9 #6).
    private const decimal OwnersPayable = 111_967.40m;

    [Fact]
    public async Task Dashboard_reconciles_to_the_golden_figures_with_a_named_hero()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var dash = await ComposeAsync(DemoSeeder.DemoOrgId, ct);

        dash.Kpis.TrustTotal.ShouldBe(483_620.69m);
        dash.Kpis.OwnersPayable.ShouldBe(OwnersPayable);
        dash.Kpis.Uncleared.ShouldBe(0m);          // no register until M4 (P45)
        dash.Kpis.UnclearedCount.ShouldBe(0);
        dash.Kpis.CollectedMtd.ShouldBe(1_380m);   // June 2026: only the Pryor payment is cash-collected

        // The banks shown sum to the trustTotal KPI (the hero ties to trustTotal).
        dash.Banks.Rows.Sum(b => b.Book).ShouldBe(dash.Kpis.TrustTotal);

        // Hero: 8 named owners + the "All other owners" roll-up, relabeled and flagged.
        dash.OwnerBalances.Rows.Count.ShouldBe(9);
        dash.OwnerBalances.Rows.ShouldContain(r => r.Name == "Hargrove Family Trust" && r.Operating == 14_820.50m);
        var rollup = dash.OwnerBalances.Rows.Single(r => r.IsRollup);
        rollup.Name.ShouldBe("All other owners");
        dash.OwnerBalances.Rows.Count(r => r.IsRollup).ShouldBe(1);
        dash.OwnerBalances.Rows.ShouldNotContain(r => !r.IsRollup && r.Name == "All other owners");

        // Honest action items, each routed.
        dash.ActionItems.ShouldNotBeEmpty();
        dash.ActionItems.ShouldAllBe(a => !string.IsNullOrEmpty(a.Route));
    }

    [Fact]
    public async Task A_second_orgs_dashboard_is_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var otherOrg = await NewOrgAsync(ct);

        var dash = await ComposeAsync(otherOrg, ct);

        dash.Kpis.TrustTotal.ShouldBe(0m);
        dash.Kpis.OwnersPayable.ShouldBe(0m);
        dash.OwnerBalances.Rows.ShouldBeEmpty();
        dash.Banks.Rows.ShouldBeEmpty();
    }

    private async Task<DashboardResponse> ComposeAsync(Guid orgId, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var service = new DashboardService(sender, new FixedClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)));

        DashboardResponse dash = null!;
        await executor.RunAsync(orgId, async () => dash = await service.ComposeAsync(ct), ct);
        return dash;
    }

    private async Task<Guid> NewOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Empty Org {orgId:N}" });
        await migratorDb.SaveChangesAsync(ct);
        return orgId;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
