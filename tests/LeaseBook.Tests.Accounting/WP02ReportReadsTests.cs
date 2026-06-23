using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-02 golden tests: management-fee income (report #8) and delinquency aging (report #7) over the
/// demo seed. Anchored figures come from GoldenFileTests (T3=1,620 / T6=2,820 owed); mgmt-fee totals
/// are lock-after-observation. All reads stay in the Accounting module's own tables (ADR-007).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class WP02ReportReadsTests(PostgresFixture fixture)
{
    // ─── Mgmt-fee income ───────────────────────────────────────────────────────────

    [Fact]
    public async Task MgmtFeeIncome_May2026_returns_per_property_pm_income()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var result = await QueryAsync(
            db => new GetManagementFeeIncomeHandler(db).Handle(new GetManagementFeeIncome(2026, 5), ct), ct);

        // Must have at least one row (the seed posts management fees in May 2026).
        result.Rows.ShouldNotBeEmpty();

        // PM isolation re-pinned at the report surface: no owner_id is exposed on the response type.
        // The record type itself has no OwnerId member — compile-time guarantee.
        // Runtime: every row's PropertyId may be null (unattributed) or a known property GUID — never
        // an owner GUID. We assert the total is positive (fees were earned).
        result.Rows.Sum(r => r.Amount).ShouldBeGreaterThan(0m);

        // Lock-after-observation: the exact figures are verified in the second fact below after first run.
        // This fact is the structural RED → GREEN gate.
    }

    [Fact]
    public async Task MgmtFeeIncome_May2026_amounts_are_locked()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var result = await QueryAsync(
            db => new GetManagementFeeIncomeHandler(db).Handle(new GetManagementFeeIncome(2026, 5), ct), ct);

        // Locked figures (observed from first run, 2026-06-22):
        // The seed posts one ManagementFeeAssessed event: AggregateOwners, null property, amount 2840.00
        // (DemoJournalSeed line: ManagementFeeAssessed(AggregateOwners, null, M(2_840m), D(5, 27), ...)).
        // That posts a single pm_income credit of 2840.00 with property_id=null.
        // Plus the balance-forward pm_income credit of 35,400.55 is dated 2026-01-31 (outside May) — not included.
        result.Rows.Count.ShouldBe(1, "one pm_income row (null property)");
        result.Rows[0].PropertyId.ShouldBeNull("fee posted to AggregateOwners with no property dimension");
        result.Rows[0].Amount.ShouldBe(2840.00m, "May 2026 management fee total");
        result.Rows.Sum(r => r.Amount).ShouldBe(2840.00m);

        // All rows carry no owner dimension (structural: MgmtFeeIncomeRow has no OwnerId property).
        // PM isolation: pm_income lines have null owner_id in the journal (verified in seed/posting code).
    }

    [Fact]
    public async Task MgmtFeeIncome_empty_month_returns_no_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        // Year 2020 has no journal entries in the demo seed.
        var result = await QueryAsync(
            db => new GetManagementFeeIncomeHandler(db).Handle(new GetManagementFeeIncome(2020, 1), ct), ct);

        result.Rows.ShouldBeEmpty();
    }

    // ─── Delinquency aging ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DelinquencyAging_AsOf_June22_2026_shows_known_late_tenants()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        // AsOf = 2026-06-22 (today per MEMORY.md currentDate).
        var result = await QueryAsync(
            db => new GetDelinquencyAgingHandler(db).Handle(new GetDelinquencyAging(new DateOnly(2026, 6, 22)), ct), ct);

        // Anchored: GoldenFileTests.Tenant_balances_match_the_dataset confirms T3=1,620 / T6=2,820 owing.
        // T1=1,450 also owes (June rent posted, not yet paid) — also surfaces in delinquency.
        result.Rows.ShouldNotBeEmpty();

        var t3 = result.Rows.SingleOrDefault(r => r.TenantId == DemoIds.T3);
        t3.ShouldNotBeNull("T3 (Aisha Bello) should appear as delinquent");
        t3!.Total.ShouldBe(1620.00m, "T3 total receivable");

        var t6 = result.Rows.SingleOrDefault(r => r.TenantId == DemoIds.T6);
        t6.ShouldNotBeNull("T6 (Lena Vasquez) should appear as delinquent");
        t6!.Total.ShouldBe(2820.00m, "T6 total receivable");
    }

    [Fact]
    public async Task DelinquencyAging_bucket_split_is_locked()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var asOf = new DateOnly(2026, 6, 22);
        var result = await QueryAsync(
            db => new GetDelinquencyAgingHandler(db).Handle(new GetDelinquencyAging(asOf), ct), ct);

        // T3 aging locked (observed 2026-06-22):
        // T3 has three relevant entries:
        //   - May charge 2026-05-01 (+1620, age 52 days → D31_60)
        //   - May payment 2026-05-30 (credit -1620, age 23 days → D1_30)
        //   - June charge 2026-06-01 (+1620, age 21 days → D1_30)
        // Bucketed by entry: D1_30 = -1620 + 1620 = 0; D31_60 = 1620. Total = 1620.
        var t3 = result.Rows.Single(r => r.TenantId == DemoIds.T3);
        t3.Current.ShouldBe(0m, "T3 current");
        t3.D1_30.ShouldBe(0m, "T3 D1_30");
        t3.D31_60.ShouldBe(1620.00m, "T3 D31_60");
        t3.D61_90.ShouldBe(0m, "T3 D61_90");
        t3.Over90.ShouldBe(0m, "T3 Over90");

        // T6 aging locked:
        // T6 May rent charged 2026-05-01 → age = 52 days → D31_60 (1410).
        // T6 June rent charged 2026-06-01 → age = 21 days → D1_30 (1410).
        // Total = 2820.
        var t6 = result.Rows.Single(r => r.TenantId == DemoIds.T6);
        t6.Current.ShouldBe(0m, "T6 current");
        t6.D1_30.ShouldBe(1410.00m, "T6 D1_30");
        t6.D31_60.ShouldBe(1410.00m, "T6 D31_60");
        t6.D61_90.ShouldBe(0m, "T6 D61_90");
        t6.Over90.ShouldBe(0m, "T6 Over90");
    }

    [Fact]
    public async Task DelinquencyAging_tenants_with_zero_balance_do_not_surface()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var result = await QueryAsync(
            db => new GetDelinquencyAgingHandler(db).Handle(new GetDelinquencyAging(new DateOnly(2026, 6, 22)), ct), ct);

        // T2 (Devon Pryor) and T4 (Cole Ramsey) have zero balance — must not appear.
        result.Rows.ShouldNotContain(r => r.TenantId == DemoIds.T2);
        result.Rows.ShouldNotContain(r => r.TenantId == DemoIds.T4);

        // T5 (The Mercer Family) has −75 (prepaid) — negative net_owed, filtered by HAVING > 0.
        result.Rows.ShouldNotContain(r => r.TenantId == DemoIds.T5);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────

    private Task SeedAsync(CancellationToken ct) => DemoSeeder.SeedAsync(fixture.Api.Services, ct);

    private async Task<T> QueryAsync<T>(Func<DbContext, Task<T>> query, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var db = scope.ServiceProvider.GetRequiredService<LeaseBook.Web.Persistence.AppDbContext>();
        T result = default!;
        await executor.RunAsync(DemoSeeder.DemoOrgId, async () => result = await query(db), ct);
        return result;
    }
}
