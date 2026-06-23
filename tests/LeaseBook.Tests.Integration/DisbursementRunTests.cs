using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
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
/// Integration tests for the M6 WP-4 owner disbursement run (DisbursementRunStrategy + ADR-018).
/// Tests run in fresh orgs to isolate from the demo org's golden figures.
/// <para>
/// Test coverage:
/// <list type="bullet">
///   <item>Full disbursement run: fee + disburse entries posted, total matches golden.</item>
///   <item>Owner below reserve floor is Excluded with reason in preview and confirm.</item>
///   <item>Trust equation holds after fee + disbursement.</item>
///   <item>Idempotent re-run: second confirm → Skipped.</item>
/// </list>
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class DisbursementRunTests(PostgresFixture fixture)
{
    private static readonly RunPeriod Period = new(2026, 6);

    // ── GOLDEN FIGURES — locked after first run (lock-after-observation) ──────
    // equity = 2100.00 (from rent charges posted), bps = 800, reserve = 200
    // fee = Round(2100 × 800/10000, 2, AwayFromZero) = 168.00
    // disburse = 2100 - 168 - 200 = 1732.00
    private const decimal GoldenRentCharged = 2100.00m;   // rent × 1 month = $2100
    private const decimal GoldenFee = 168.00m;
    private const decimal GoldenDisburse = 1732.00m;

    [Fact]
    public async Task Full_disbursement_run_posts_fee_and_disburse_and_total_matches_golden()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(rent: 2100m, mgmtFeeBps: 800, reserve: 200m, ct);

        // Post rent charges to give the owner equity.
        await PostRentChargeAsync(ctx, GoldenRentCharged, ct);

        RunResult? result = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            var preview = await engine.PreviewAsync(RunType.Disbursement, Period, ct);
            preview.Rows.Count.ShouldBeGreaterThan(0, "at least one eligible owner expected");
            preview.Rows.ShouldContain(r => r.TargetId == ctx.OwnerId && r.ExcludedReason == null);
            preview.Rows.First(r => r.TargetId == ctx.OwnerId).Amount.ShouldBe(GoldenDisburse,
                "preview disburse amount must match golden");

            var targets = preview.Rows
                .Where(r => r.ExcludedReason == null)
                .Select(r => r.TargetId)
                .ToList();
            result = await engine.ConfirmAsync(RunType.Disbursement, Period, targets, ct);
        }, ct);

        result.ShouldNotBeNull();
        result!.Posted.ShouldBeGreaterThan(0);
        result.Total.ShouldBe(GoldenDisburse * result.Posted,
            $"expected golden disburse total of {GoldenDisburse} × {result.Posted} owners");
    }

    [Fact]
    public async Task Owner_below_reserve_floor_is_excluded_with_reason_in_preview_and_confirm()
    {
        // rent=100, bps=800, reserve=500 → fee=8, net=92, disburse=92-500=-408 → excluded
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(rent: 100m, mgmtFeeBps: 800, reserve: 500m, ct);
        await PostRentChargeAsync(ctx, 100m, ct);

        RunPreview? preview = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            preview = await engine.PreviewAsync(RunType.Disbursement, Period, ct);
        }, ct);

        preview.ShouldNotBeNull();
        var row = preview!.Rows.FirstOrDefault(r => r.TargetId == ctx.OwnerId);
        row.ShouldNotBeNull("owner row must appear in preview even when excluded");
        row!.ExcludedReason.ShouldBe("below_reserve_floor");
        row.Amount.ShouldBe(0m);

        // Confirm with the excluded owner — must produce Excluded, not error.
        RunResult? result = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            result = await engine.ConfirmAsync(RunType.Disbursement, Period, [ctx.OwnerId], ct);
        }, ct);

        result.ShouldNotBeNull();
        result!.Excluded.ShouldBeGreaterThan(0, "below-reserve owner must be Excluded on confirm");
        result.Posted.ShouldBe(0);
    }

    [Fact]
    public async Task Trust_equation_holds_after_fee_and_disbursement()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(rent: 2100m, mgmtFeeBps: 800, reserve: 200m, ct);
        await PostRentChargeAsync(ctx, GoldenRentCharged, ct);

        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            var preview = await engine.PreviewAsync(RunType.Disbursement, Period, ct);
            var targets = preview.Rows
                .Where(r => r.ExcludedReason == null)
                .Select(r => r.TargetId)
                .ToList();
            await engine.ConfirmAsync(RunType.Disbursement, Period, targets, ct);
        }, ct);

        TrustEquationResponse? equation = null;
        await DispatchAsync(ctx.OrgId, async (s, _) =>
        {
            equation = await s.Query(new GetTrustEquation(), ct);
        }, ct);

        equation.ShouldNotBeNull();
        equation!.Rows.ShouldAllBe(r => r.Variance == 0m,
            "trust equation must balance after fee + disbursement posting");
    }

    [Fact]
    public async Task Idempotent_rerun_second_confirm_posts_nothing_new()
    {
        // After disbursement the owner's cash equity is spent. A second confirm on the same
        // owner targets produces zero new postings: either Skipped (if the source_ref is hit)
        // or Excluded (if the equity is now ≤ reserve floor). Either way Posted must be 0.
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(rent: 2100m, mgmtFeeBps: 800, reserve: 200m, ct);
        await PostRentChargeAsync(ctx, GoldenRentCharged, ct);

        // First confirm.
        IReadOnlyList<Guid> targets = [];
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            var preview = await engine.PreviewAsync(RunType.Disbursement, Period, ct);
            targets = preview.Rows
                .Where(r => r.ExcludedReason == null)
                .Select(r => r.TargetId)
                .ToList();
            await engine.ConfirmAsync(RunType.Disbursement, Period, targets, ct);
        }, ct);

        // Second confirm on same targets — must produce no new posts.
        RunResult? secondResult = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            secondResult = await engine.ConfirmAsync(RunType.Disbursement, Period, targets, ct);
        }, ct);

        secondResult.ShouldNotBeNull();
        secondResult!.Posted.ShouldBe(0, "second run must not create new postings");
        secondResult.Total.ShouldBe(0m);

        // Verify idempotency at the source_ref level: restore owner equity via OwnerContribution
        // (directly through IAccountingEvents — no command needed), then run a third confirm with
        // the same targets. The source_ref index catches the repeat → all items must be Skipped
        // (DuplicateSourceRefException), not re-posted.
        await DispatchAsync(ctx.OrgId, async (_, sp) =>
        {
            var events = sp.GetRequiredService<IAccountingEvents>();
            await events.PostAsync(
                new OwnerContribution(
                    ctx.OwnerId, null,
                    new Money(GoldenDisburse + GoldenFee),
                    new DateOnly(Period.Year, Period.Month, 10),
                    ctx.TrustBankId,
                    $"test contribution restoring equity for source_ref idempotency check"),
                ct);
        }, ct);

        RunResult? thirdResult = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            thirdResult = await engine.ConfirmAsync(RunType.Disbursement, Period, targets, ct);
        }, ct);

        thirdResult.ShouldNotBeNull();
        thirdResult!.Posted.ShouldBe(0, "third run with same source_ref must not post — DuplicateSourceRef is the backstop");
        thirdResult.Skipped.ShouldBeGreaterThan(0, "at least one item must be Skipped (DuplicateSourceRefException) not re-posted");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Ctx(
        Guid OrgId,
        Guid TrustBankId,
        Guid OwnerId,
        Guid TenantId,
        Guid LeaseId);

    private async Task<Ctx> SetupAsync(decimal rent, int? mgmtFeeBps, decimal reserve, CancellationToken ct)
    {
        var orgId = await NewOrgAsync(ct);
        Guid trustBankId = default, ownerId = default, tenantId = default, leaseId = default;

        await DispatchAsync(orgId, async (s, _) =>
        {
            var trust = await s.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct);
            trustBankId = trust.Id;

            ownerId = await s.Send(new CreateOwner("Disbursement Test Owner", null, null, null, mgmtFeeBps, reserve), ct);
            var propId = await s.Send(new CreateProperty(ownerId, "100 Test St", "Raleigh", "NC", null, null), ct);
            var unitId = await s.Send(new CreateUnit(propId, "#1", rent, "occupied"), ct);
            tenantId = await s.Send(new CreateTenant("Disburse Tenant", null, null, "current"), ct);
            leaseId = await s.Send(new CreateLease(
                tenantId, unitId,
                new DateOnly(2025, 1, 1), new DateOnly(2027, 12, 31),
                rent, rent, "active"), ct);
        }, ct);

        return new Ctx(orgId, trustBankId, ownerId, tenantId, leaseId);
    }

    /// <summary>
    /// Posts a rent charge then a full payment to give the owner cash equity.
    /// RentCharged is accrual-only; PaymentReceived auto-splits receivable → owner_equity cash credit.
    /// </summary>
    private async Task PostRentChargeAsync(Ctx ctx, decimal amount, CancellationToken ct)
    {
        await DispatchAsync(ctx.OrgId, async (s, _) =>
        {
            // Post rent charge (accrual) to create tenant receivable.
            await s.Send(
                new AddCharge(
                    ctx.TenantId, amount,
                    new DateOnly(Period.Year, Period.Month, 1),
                    "rent", null,
                    $"rent:{Period.Key}:lease={ctx.LeaseId}"),
                ct);

            // Post full payment (cash) — auto-splits receivable → cash owner_equity credit.
            await s.Send(
                new RecordPayment(
                    ctx.TenantId, amount,
                    new DateOnly(Period.Year, Period.Month, 5),
                    "check", ctx.TrustBankId,
                    "Rent payment",
                    $"payment:{Period.Key}:tenant={ctx.TenantId}"),
                ct);
        }, ct);
    }

    private async Task<Guid> NewOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Disbursement Test Org {orgId:N}" });
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
}
