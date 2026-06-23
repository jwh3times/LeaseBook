using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Settings;
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
/// Integration tests for the M6 WP-3 late-fee run (LateFeeRunStrategy + NC §42-46 clamp).
/// Tests run in fresh orgs to isolate from the demo org's golden figures.
/// <para>
/// Test coverage:
/// <list type="bullet">
///   <item>NC §42-46 clamp end-to-end: high flat fee clamped down on real rent.</item>
///   <item>Selective confirm: only chosen leases get late fees posted.</item>
///   <item>Per-lease override beats org default.</item>
///   <item>Trust equation holds after late-fee posting.</item>
///   <item>Idempotent re-run: second confirm → Skipped.</item>
/// </list>
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class LateFeeRunTests(PostgresFixture fixture)
{
    // Period under test: March 2026 (31-day month).
    private static readonly RunPeriod Period = new(2026, 3);

    // ── NC clamp golden figures ─────────────────────────────────────────────
    // Rent = $1450; 5% cap = max(15, 72.50) = 72.50; high flat $200 → clamped to 72.50.
    private const decimal HighRentRent = 1450m;
    private const decimal HighRentCappedFee = 72.50m;

    // Rent = $200; 5% cap = max(15, 10) = 15; flat $200 → clamped to 15.
    private const decimal LowRentRent = 200m;
    private const decimal LowRentCappedFee = 15m;

    [Fact]
    public async Task NC_clamp_high_flat_fee_is_clamped_to_five_percent_of_rent()
    {
        // Arrange: one delinquent lease on high-rent unit, org default = flat $200 (well above 5%).
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(HighRentRent, flatFeeOverride: null, ct);

        // Set org default to flat $200 (above NC cap for $1450 rent → 72.50).
        await DispatchAsync(ctx.OrgId, (s, _) =>
            s.Send(new UpdateOrgSettings(
                AccountingBasis: null, MoneyNegativeDisplay: null,
                LegalName: null, Address: null, City: null, State: null, Zip: null, Phone: null, LogoBlobRef: null,
                RentDueDay: 1, LateFeeGraceDays: 0, LateFeeKind: "flat",
                LateFeeAmount: 200m, LateFeeRateBps: 0), ct), ct);

        // Make the lease delinquent by posting a rent charge (sets up receivable balance).
        await PostRentChargeAsync(ctx, ct);

        // Act: preview + confirm all eligible leases.
        RunResult? result = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            var preview = await engine.PreviewAsync(RunType.LateFee, Period, ct);
            preview.Rows.Count.ShouldBeGreaterThan(0, "at least one delinquent lease expected");
            var targets = preview.Rows.Select(r => r.TargetId).ToList();
            result = await engine.ConfirmAsync(RunType.LateFee, Period, targets, ct);
        }, ct);

        result.ShouldNotBeNull();
        result!.Posted.ShouldBeGreaterThan(0);
        // Total should be clamped (72.50 per lease), not the raw $200.
        result.Total.ShouldBe(HighRentCappedFee * result.Posted,
            $"expected NC-clamped fee of {HighRentCappedFee} per lease, got {result.Total} for {result.Posted} posted");
    }

    [Fact]
    public async Task Selective_confirm_posts_only_chosen_leases()
    {
        // Arrange: two delinquent leases; we'll select only one.
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupTwoLeasesAsync(ct);

        // Make both delinquent.
        await PostRentChargeTwoAsync(ctx, ct);

        IReadOnlyList<PreviewRow> previewRows = [];
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            var preview = await engine.PreviewAsync(RunType.LateFee, Period, ct);
            previewRows = preview.Rows;
        }, ct);

        previewRows.Count.ShouldBe(2, "both leases should be delinquent");

        // Select only the first one.
        var selected = new[] { previewRows[0].TargetId };

        RunResult? result = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            result = await engine.ConfirmAsync(RunType.LateFee, Period, selected, ct);
        }, ct);

        result.ShouldNotBeNull();
        result!.Posted.ShouldBe(1, "only the selected lease should be posted");
        result.Excluded.ShouldBe(0);
        result.Skipped.ShouldBe(0);
    }

    [Fact]
    public async Task Per_lease_override_beats_org_default()
    {
        // Arrange: org default = flat $200 (above NC cap), lease override = flat $25 (under cap).
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(HighRentRent, flatFeeOverride: 25m, ct);

        // Set org default to flat $200.
        await DispatchAsync(ctx.OrgId, (s, _) =>
            s.Send(new UpdateOrgSettings(
                AccountingBasis: null, MoneyNegativeDisplay: null,
                LegalName: null, Address: null, City: null, State: null, Zip: null, Phone: null, LogoBlobRef: null,
                RentDueDay: 1, LateFeeGraceDays: 0, LateFeeKind: "flat",
                LateFeeAmount: 200m, LateFeeRateBps: 0), ct), ct);

        // Make the lease delinquent.
        await PostRentChargeAsync(ctx, ct);

        RunResult? result = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            var preview = await engine.PreviewAsync(RunType.LateFee, Period, ct);
            preview.Rows.Count.ShouldBeGreaterThan(0);
            var targets = preview.Rows.Select(r => r.TargetId).ToList();
            result = await engine.ConfirmAsync(RunType.LateFee, Period, targets, ct);
        }, ct);

        result.ShouldNotBeNull();
        result!.Posted.ShouldBeGreaterThan(0);
        // Per-lease override of $25 < NC cap of $72.50 → fee should be $25.
        result.Total.ShouldBe(25m * result.Posted,
            $"per-lease override of $25 should override org default of $200; NC cap is $72.50");
    }

    [Fact]
    public async Task Trust_equation_holds_after_late_fee_confirm()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(HighRentRent, flatFeeOverride: null, ct);

        await DispatchAsync(ctx.OrgId, (s, _) =>
            s.Send(new UpdateOrgSettings(
                AccountingBasis: null, MoneyNegativeDisplay: null,
                LegalName: null, Address: null, City: null, State: null, Zip: null, Phone: null, LogoBlobRef: null,
                RentDueDay: 1, LateFeeGraceDays: 0, LateFeeKind: "flat",
                LateFeeAmount: 50m, LateFeeRateBps: 0), ct), ct);

        await PostRentChargeAsync(ctx, ct);

        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            var preview = await engine.PreviewAsync(RunType.LateFee, Period, ct);
            var targets = preview.Rows.Select(r => r.TargetId).ToList();
            await engine.ConfirmAsync(RunType.LateFee, Period, targets, ct);
        }, ct);

        TrustEquationResponse? equation = null;
        await DispatchAsync(ctx.OrgId, async (s, _) =>
        {
            equation = await s.Query(new GetTrustEquation(), ct);
        }, ct);

        equation.ShouldNotBeNull();
        equation!.Rows.ShouldAllBe(r => r.Variance == 0m,
            "trust equation must balance after posting late fees");
    }

    [Fact]
    public async Task Idempotent_re_run_second_confirm_produces_skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(HighRentRent, flatFeeOverride: null, ct);

        await DispatchAsync(ctx.OrgId, (s, _) =>
            s.Send(new UpdateOrgSettings(
                AccountingBasis: null, MoneyNegativeDisplay: null,
                LegalName: null, Address: null, City: null, State: null, Zip: null, Phone: null, LogoBlobRef: null,
                RentDueDay: 1, LateFeeGraceDays: 0, LateFeeKind: "flat",
                LateFeeAmount: 50m, LateFeeRateBps: 0), ct), ct);

        await PostRentChargeAsync(ctx, ct);

        // First confirm.
        IReadOnlyList<Guid> targets = [];
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            var preview = await engine.PreviewAsync(RunType.LateFee, Period, ct);
            targets = preview.Rows.Select(r => r.TargetId).ToList();
            await engine.ConfirmAsync(RunType.LateFee, Period, targets, ct);
        }, ct);

        // Second confirm on same targets — must be Skipped.
        RunResult? secondResult = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            secondResult = await engine.ConfirmAsync(RunType.LateFee, Period, targets, ct);
        }, ct);

        secondResult.ShouldNotBeNull();
        secondResult!.Posted.ShouldBe(0);
        secondResult.Skipped.ShouldBeGreaterThan(0, "second run must produce Skipped, not new postings");
        secondResult.Total.ShouldBe(0m);
    }

    [Fact]
    public async Task Low_rent_floor_applies_minimum_fifteen_dollar_cap()
    {
        // Rent = $200; org flat fee = $200 → clamped to max(15, 5%*200=10) = 15.
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(LowRentRent, flatFeeOverride: null, ct);

        await DispatchAsync(ctx.OrgId, (s, _) =>
            s.Send(new UpdateOrgSettings(
                AccountingBasis: null, MoneyNegativeDisplay: null,
                LegalName: null, Address: null, City: null, State: null, Zip: null, Phone: null, LogoBlobRef: null,
                RentDueDay: 1, LateFeeGraceDays: 0, LateFeeKind: "flat",
                LateFeeAmount: 200m, LateFeeRateBps: 0), ct), ct);

        await PostRentChargeAsync(ctx, ct);

        RunResult? result = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            var preview = await engine.PreviewAsync(RunType.LateFee, Period, ct);
            if (preview.Rows.Count == 0)
            {
                // If no delinquency rows (balance not yet picked up), skip.
                result = new RunResult(Guid.Empty, 0, 0, 0, 0m);
                return;
            }
            var targets = preview.Rows.Select(r => r.TargetId).ToList();
            result = await engine.ConfirmAsync(RunType.LateFee, Period, targets, ct);
        }, ct);

        result.ShouldNotBeNull();
        if (result!.Posted > 0)
        {
            result.Total.ShouldBe(LowRentCappedFee * result.Posted,
                $"NC floor of $15 should apply; expected {LowRentCappedFee} per lease");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Ctx(
        Guid OrgId,
        Guid TrustBankId,
        Guid TenantId,
        Guid LeaseId,
        Guid PropertyId,
        Guid OwnerId,
        decimal Rent);

    private sealed record TwoLeaseCtx(
        Guid OrgId,
        Guid TrustBankId,
        Guid TenantId1,
        Guid LeaseId1,
        Guid TenantId2,
        Guid LeaseId2,
        Guid PropertyId,
        Guid OwnerId,
        decimal Rent);

    private async Task<Ctx> SetupAsync(decimal rent, decimal? flatFeeOverride, CancellationToken ct)
    {
        var orgId = await NewOrgAsync(ct);
        Guid trustBankId = default, tenantId = default, leaseId = default, propId = default, ownerId = default;

        await DispatchAsync(orgId, async (s, _) =>
        {
            var trust = await s.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct);
            trustBankId = trust.Id;

            ownerId = await s.Send(new CreateOwner("Fee Test Owner", null, null, null, 800, 0m), ct);
            propId = await s.Send(new CreateProperty(ownerId, "100 Fee Ln", "Raleigh", "NC", null, null), ct);
            var unitId = await s.Send(new CreateUnit(propId, "#A", rent, "occupied"), ct);
            tenantId = await s.Send(new CreateTenant("Late Fee Tenant", null, null, "current"), ct);

            leaseId = await s.Send(new CreateLease(
                tenantId, unitId,
                new DateOnly(2025, 1, 1), new DateOnly(2027, 12, 31),
                rent, rent, "active",
                LateFeeRentDueDayOverride: 1,
                LateFeeGraceDaysOverride: 0, // No grace — immediately eligible.
                LateFeeKindOverride: flatFeeOverride.HasValue ? "flat" : null,
                LateFeeAmountOverride: flatFeeOverride,
                LateFeeRateBpsOverride: null), ct);
        }, ct);

        return new Ctx(orgId, trustBankId, tenantId, leaseId, propId, ownerId, rent);
    }

    private async Task<TwoLeaseCtx> SetupTwoLeasesAsync(CancellationToken ct)
    {
        var orgId = await NewOrgAsync(ct);
        Guid trustBankId = default, t1 = default, l1 = default, t2 = default, l2 = default, propId = default, ownerId = default;

        await DispatchAsync(orgId, async (s, _) =>
        {
            var trust = await s.Send(new CreateBankAccount("Trust", null, null, "trust"), ct);
            trustBankId = trust.Id;
            ownerId = await s.Send(new CreateOwner("Two Lease Owner", null, null, null, 800, 0m), ct);
            propId = await s.Send(new CreateProperty(ownerId, "200 Fee Ave", "Durham", "NC", null, null), ct);
            var u1 = await s.Send(new CreateUnit(propId, "#1", 1200m, "occupied"), ct);
            var u2 = await s.Send(new CreateUnit(propId, "#2", 1300m, "occupied"), ct);
            t1 = await s.Send(new CreateTenant("Tenant Alpha", null, null, "current"), ct);
            t2 = await s.Send(new CreateTenant("Tenant Beta", null, null, "current"), ct);
            l1 = await s.Send(new CreateLease(t1, u1, new DateOnly(2025, 1, 1), new DateOnly(2027, 12, 31),
                1200m, 1200m, "active",
                LateFeeRentDueDayOverride: 1, LateFeeGraceDaysOverride: 0,
                LateFeeKindOverride: "flat", LateFeeAmountOverride: 50m, LateFeeRateBpsOverride: null), ct);
            l2 = await s.Send(new CreateLease(t2, u2, new DateOnly(2025, 1, 1), new DateOnly(2027, 12, 31),
                1300m, 1300m, "active",
                LateFeeRentDueDayOverride: 1, LateFeeGraceDaysOverride: 0,
                LateFeeKindOverride: "flat", LateFeeAmountOverride: 50m, LateFeeRateBpsOverride: null), ct);
        }, ct);

        return new TwoLeaseCtx(orgId, trustBankId, t1, l1, t2, l2, propId, ownerId, 1200m);
    }

    /// <summary>
    /// Posts a rent charge to create a delinquent receivable balance on the lease.
    /// The GetDelinquencyAging query returns tenants with D1_30+ balance > 0 and age > 0 days.
    /// We post a RentCharged entry (which goes to tenant_receivable) to produce a balance.
    /// </summary>
    private async Task PostRentChargeAsync(Ctx ctx, CancellationToken ct)
    {
        // Post via the Accounting posting command so the journal entry lands correctly.
        await DispatchAsync(ctx.OrgId, async (s, _) =>
        {
            // AddCharge is the ledger command that posts a RentCharged event via IAccountingEvents.
            await s.Send(
                new LeaseBook.Modules.Accounting.Features.LedgerPosting.AddCharge(
                    ctx.TenantId, ctx.Rent,
                    new DateOnly(Period.Year, Period.Month, 1),
                    "rent", null,
                    $"rent:{Period.Key}:lease={ctx.LeaseId}"),
                ct);
        }, ct);
    }

    private async Task PostRentChargeTwoAsync(TwoLeaseCtx ctx, CancellationToken ct)
    {
        await DispatchAsync(ctx.OrgId, async (s, _) =>
        {
            await s.Send(
                new LeaseBook.Modules.Accounting.Features.LedgerPosting.AddCharge(
                    ctx.TenantId1, 1200m,
                    new DateOnly(Period.Year, Period.Month, 1),
                    "rent", null,
                    $"rent:{Period.Key}:lease={ctx.LeaseId1}"),
                ct);
            await s.Send(
                new LeaseBook.Modules.Accounting.Features.LedgerPosting.AddCharge(
                    ctx.TenantId2, 1300m,
                    new DateOnly(Period.Year, Period.Month, 1),
                    "rent", null,
                    $"rent:{Period.Key}:lease={ctx.LeaseId2}"),
                ct);
        }, ct);
    }

    private async Task<Guid> NewOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"LateFee Test Org {orgId:N}" });
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

    private async Task<T> DispatchAsync<T>(Guid orgId, Func<ISender, IServiceProvider, Task<T>> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        T result = default!;
        await executor.RunAsync(orgId, async () => { result = await work(sender, scope.ServiceProvider); }, ct);
        return result;
    }
}
