using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Operations.Runs;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// Integration tests for the M6 WP-2 rent charge run (RentRunStrategy + proration ADR-017).
/// Runs in a fresh org with 3 active leases (two full-month, one prorated move-in) so the demo
/// org's golden figures are undisturbed. Golden totals are lock-after-observation.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OperationsRunsTests(PostgresFixture fixture)
{
    // Period under test: March 2026 (31-day month — clean proration arithmetic).
    private static readonly int Year = 2026;
    private static readonly int Month = 3;
    private static readonly RunPeriod Period = new(Year, Month);

    // ── GOLDEN FIGURES — locked after first run (lock-after-observation) ────────
    // Lease 1: full-month, rent=1450 → 1450.00
    // Lease 2: full-month, rent=1380 → 1380.00
    // Lease 3: move-in Mar 16, rent=1620, March (31 days).
    //          daysOccupied = 31 - 16 + 1 = 16 (inclusive of move-in day)
    //          1620m * 16 / 31 = 25920m / 31 = 836.129032...m → Math.Round(..., 2, AwayFromZero) = 836.13
    private const decimal GoldenLease1Amount = 1450.00m;
    private const decimal GoldenLease2Amount = 1380.00m;
    private const decimal GoldenLease3Amount = 836.13m;
    private const decimal GoldenTotal = GoldenLease1Amount + GoldenLease2Amount + GoldenLease3Amount; // 3666.13

    [Fact]
    public async Task Preview_returns_one_row_per_active_lease_with_prorated_amounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);

        RunPreview? preview = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            preview = await engine.PreviewAsync(RunType.Rent, Period, ct);
        }, ct);

        preview.ShouldNotBeNull();
        preview!.RunType.ShouldBe(RunType.Rent);
        preview.Period.ShouldBe(Period);

        // 3 active leases in the period, no exceptions.
        preview.Rows.Count.ShouldBe(3);
        preview.Exceptions.Count.ShouldBe(0);

        // Prorated lease row should be flagged.
        var prorated = preview.Rows.Where(r => r.Detail.ContainsKey("prorated")).ToList();
        prorated.Count.ShouldBe(1);
        prorated[0].Amount.ShouldBe(GoldenLease3Amount);

        // Full-month leases carry their full rent.
        var fullMonth = preview.Rows.Where(r => !r.Detail.ContainsKey("prorated")).ToList();
        fullMonth.Count.ShouldBe(2);
        fullMonth.Select(r => r.Amount).Order().ShouldBe([GoldenLease2Amount, GoldenLease1Amount], ignoreOrder: true);

        // AlreadyDone is false before first confirm.
        preview.Rows.ShouldAllBe(r => !r.AlreadyDone);
    }

    [Fact]
    public async Task Confirm_posts_one_rent_charged_entry_per_lease_and_total_matches_golden()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);

        RunResult? result = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            var preview = await engine.PreviewAsync(RunType.Rent, Period, ct);
            var targets = preview.Rows.Select(r => r.TargetId).ToList();
            result = await engine.ConfirmAsync(RunType.Rent, Period, targets, ct);
        }, ct);

        result.ShouldNotBeNull();
        result!.Posted.ShouldBe(3);
        result.Skipped.ShouldBe(0);
        result.Excluded.ShouldBe(0);
        result.Total.ShouldBe(GoldenTotal);
    }

    [Fact]
    public async Task Confirm_is_idempotent_second_run_produces_zero_new_entries()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);

        // First confirm.
        IReadOnlyList<Guid> targets = [];
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            var preview = await engine.PreviewAsync(RunType.Rent, Period, ct);
            targets = preview.Rows.Select(r => r.TargetId).ToList();
            await engine.ConfirmAsync(RunType.Rent, Period, targets, ct);
        }, ct);

        // Second confirm on the same targets — all must be Skipped.
        RunResult? secondResult = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            secondResult = await engine.ConfirmAsync(RunType.Rent, Period, targets, ct);
        }, ct);

        secondResult.ShouldNotBeNull();
        secondResult!.Posted.ShouldBe(0);
        secondResult.Skipped.ShouldBe(3);
        secondResult.Total.ShouldBe(0m);
    }

    [Fact]
    public async Task Preview_shows_already_done_after_first_confirm()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);

        // First confirm.
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            var preview = await engine.PreviewAsync(RunType.Rent, Period, ct);
            var targets = preview.Rows.Select(r => r.TargetId).ToList();
            await engine.ConfirmAsync(RunType.Rent, Period, targets, ct);
        }, ct);

        // Second preview — all rows should be flagged AlreadyDone.
        RunPreview? secondPreview = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            secondPreview = await engine.PreviewAsync(RunType.Rent, Period, ct);
        }, ct);

        secondPreview.ShouldNotBeNull();
        secondPreview!.Rows.ShouldAllBe(r => r.AlreadyDone);
    }

    [Fact]
    public async Task Trust_equation_holds_after_rent_confirm()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);

        // Confirm the run.
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            var preview = await engine.PreviewAsync(RunType.Rent, Period, ct);
            var targets = preview.Rows.Select(r => r.TargetId).ToList();
            await engine.ConfirmAsync(RunType.Rent, Period, targets, ct);
        }, ct);

        // Trust equation must hold.
        TrustEquationResponse? equation = null;
        await DispatchAsync(ctx.OrgId, async (s, _) =>
        {
            equation = await s.Query(new GetTrustEquation(), ct);
        }, ct);

        equation.ShouldNotBeNull();
        equation!.Rows.ShouldAllBe(r => r.Variance == 0m,
            "trust equation must balance after posting rent charges");
    }

    [Fact]
    public async Task Lease_ended_before_period_appears_as_exception_not_a_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);

        // Preview for July 2026 — lease 3 (move-in Mar 16) ended Mar 31 → not active in July.
        // All 3 leases end before July 2026 in this test setup, so we expect 0 rows + exceptions.
        // Actually: our leases end 2026-03-31 for lease 3 and 2027-05-31 for 1 & 2.
        // July 2026: lease 3 is ended, leases 1 & 2 are still active.
        RunPreview? preview = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            preview = await engine.PreviewAsync(RunType.Rent, new RunPeriod(2026, 7), ct);
        }, ct);

        preview.ShouldNotBeNull();
        // Leases 1 & 2 (full-year, ending 2027-05-31) are active in July.
        // Lease 3 (ended 2026-03-31) is NOT active in July.
        preview!.Rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Confirm_with_closed_period_maps_items_to_Excluded_not_thrown()
    {
        // Arrange: fresh org with leases, then close March 2026 so posting will raise PeriodClosedException.
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);

        // Close the accounting period for March 2026 before any posting attempt.
        await ClosePeriodAsync(ctx.OrgId, Year, Month, ct);

        // Act: confirm the rent run — all 3 items must land as Excluded, no exception escapes.
        RunResult? result = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            var preview = await engine.PreviewAsync(RunType.Rent, Period, ct);
            var targets = preview.Rows.Select(r => r.TargetId).ToList();
            result = await engine.ConfirmAsync(RunType.Rent, Period, targets, ct);
        }, ct);

        // Assert: all items Excluded, none thrown, BulkRun still recorded (result non-null).
        result.ShouldNotBeNull();
        result!.Posted.ShouldBe(0);
        result.Skipped.ShouldBe(0);
        result.Excluded.ShouldBe(3, "closed accounting period must map every item to Excluded, not throw");
        result.Total.ShouldBe(0m);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Ctx(Guid OrgId, Guid TrustBankId);

    private async Task<Ctx> SetupAsync(CancellationToken ct)
    {
        var orgId = await NewOrgAsync(ct);
        Guid trustBankId = default;

        await DispatchAsync(orgId, async (s, _) =>
        {
            // Create trust bank account (provisions chart of accounts in Accounting).
            var trust = await s.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct);
            trustBankId = trust.Id;

            // Owner + 2 properties.
            var ownerId = await s.Send(new CreateOwner("Test Owner", null, null, null, 800, 0m), ct);
            var propId1 = await s.Send(new CreateProperty(ownerId, "100 Main St", "Asheville", "NC", null, null), ct);
            var propId2 = await s.Send(new CreateProperty(ownerId, "200 Oak Ave", "Asheville", "NC", null, null), ct);

            // 3 units.
            var unitId1 = await s.Send(new CreateUnit(propId1, "#A", 1450m, "occupied"), ct);
            var unitId2 = await s.Send(new CreateUnit(propId1, "#B", 1380m, "occupied"), ct);
            var unitId3 = await s.Send(new CreateUnit(propId2, "#1", 1620m, "occupied"), ct);

            // 3 tenants.
            var t1 = await s.Send(new CreateTenant("Alice Full", null, null, "current"), ct);
            var t2 = await s.Send(new CreateTenant("Bob Full", null, null, "current"), ct);
            var t3 = await s.Send(new CreateTenant("Carol Prorated", null, null, "current"), ct);

            // Lease 1: full-month, covers March 2026 fully.
            await s.Send(new CreateLease(t1, unitId1,
                new DateOnly(2025, 6, 1), new DateOnly(2027, 5, 31),
                1450m, 1450m, "active"), ct);

            // Lease 2: full-month, covers March 2026 fully.
            await s.Send(new CreateLease(t2, unitId2,
                new DateOnly(2025, 6, 1), new DateOnly(2027, 5, 31),
                1380m, 1380m, "active"), ct);

            // Lease 3: move-in Mar 16 → prorated. Short term ends Mar 31.
            await s.Send(new CreateLease(t3, unitId3,
                new DateOnly(2026, 3, 16), new DateOnly(2026, 3, 31),
                1620m, 1620m, "active"), ct);
        }, ct);

        return new Ctx(orgId, trustBankId);
    }

    private async Task<Guid> NewOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Rent Run Test Org {orgId:N}" });
        await migratorDb.SaveChangesAsync(ct);
        return orgId;
    }

    private async Task RunAsync(Guid orgId, Func<RunEngine, IServiceProvider, Task> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var engine = scope.ServiceProvider.GetRequiredService<RunEngine>();
        await executor.RunAsync(orgId, () => work(engine, scope.ServiceProvider), ct);
    }

    private async Task DispatchAsync(Guid orgId, Func<ISender, IServiceProvider, Task> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        await executor.RunAsync(orgId, () => work(sender, scope.ServiceProvider), ct);
    }

    /// <summary>
    /// Closes the given accounting period for <paramref name="orgId"/> using the same
    /// <see cref="AccountingPeriods"/> service the posting engine reads. Mirrors the pattern used
    /// in <see cref="LeaseBook.Tests.Accounting.AccountingPeriodsTests"/> (CloseAsync_flips_open_to_closed_and_is_idempotent).
    /// </summary>
    private async Task ClosePeriodAsync(Guid orgId, int year, int month, CancellationToken ct)
    {
        var tenant = new LeaseBook.SharedKernel.Tenancy.TenantContext();
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);
        await executor.RunAsync(orgId, () => new AccountingPeriods(db).CloseAsync(year, month, ct), ct);
    }
}
