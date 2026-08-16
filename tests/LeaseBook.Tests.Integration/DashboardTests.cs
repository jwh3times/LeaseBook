using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Operations.Features.Dashboard;
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
/// figures (trustTotal excludes PM operating cash; available-to-disburse follows the Operations
/// fee-then-reserve policy),
/// the hero is named with the relabeled roll-up and ties to the bank total, and a second org sees none of
/// it. Tenant payments received are pinned with a fixed clock (June 2026).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class DashboardTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Dashboard_reconciles_to_the_golden_figures_with_a_named_hero()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        // Inline composition so we can reuse scope's sender/executor for the structural uncleared assertion.
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var service = new DashboardService(
            sender,
            new FixedClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)),
            scope.ServiceProvider.GetRequiredService<DashboardMetricsService>());

        DashboardResponse dash = null!;
        await executor.RunAsSystemAsync(DemoSeeder.DemoOrgId, "test-harness", async () => dash = await service.ComposeAsync(ct), ct);

        // Fiduciary cash only: Operating Trust + Security Deposit Trust. PM Operating's $38,240.55
        // belongs to the management company and must not inflate the trust position.
        dash.Kpis.TrustTotal.ShouldBe(445_380.14m);
        dash.Kpis.AvailableToDisburse.ShouldBe(103_010.01m);
        dash.Kpis.TenantPaymentsMtd.ShouldBe(1_380m); // June receipt, regardless of the charge period
        dash.Kpis.ScheduledRent.ShouldBe(10_855m);    // June billing baseline, not a receipt denominator

        // The KPI is the honest sum across bank accounts (not hardcoded) — proven structurally via a
        // sibling GetBankBalances query (DashboardBankRow carries UnclearedCount, not Uncleared dollar net).
        BankBalancesResponse bal = null!;
        await executor.RunAsSystemAsync(DemoSeeder.DemoOrgId, "test-harness",
            async () => bal = await sender.Query(new GetBankBalances(), ct), ct);
        dash.Kpis.Uncleared.ShouldBe(bal.Rows.Where(r => r.IsTrust).Sum(r => r.Uncleared));
        dash.Kpis.UnclearedCount.ShouldBe(bal.Rows.Where(r => r.IsTrust).Sum(r => r.UnclearedCount));
        // Golden-locked: Operating Trust has 3 uncleared items; the dashboard surfaces a non-zero total now.
        dash.Banks.Rows.Single(b => b.Name == "Operating Trust").UnclearedCount.ShouldBe(3);
        dash.Kpis.UnclearedCount.ShouldBeGreaterThan(0);

        // The fiduciary banks shown sum to the trustTotal KPI; PM operating is deliberately absent.
        dash.Banks.Rows.Sum(b => b.Book).ShouldBe(dash.Kpis.TrustTotal);
        dash.Banks.Rows.ShouldNotContain(b => b.Name == "PM Operating");

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
        dash.Kpis.AvailableToDisburse.ShouldBe(0m);
        dash.OwnerBalances.Rows.ShouldBeEmpty();
        dash.Banks.Rows.ShouldBeEmpty();
    }

    private async Task<DashboardResponse> ComposeAsync(Guid orgId, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var service = new DashboardService(
            sender,
            new FixedClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)),
            scope.ServiceProvider.GetRequiredService<DashboardMetricsService>());

        DashboardResponse dash = null!;
        await executor.RunAsSystemAsync(orgId, "test-harness", async () => dash = await service.ComposeAsync(ct), ct);
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
