using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// TDD integration tests for Fix A: the structural period guard that prevents cross-source
/// double-charging. A manual charge with a non-bulk source_ref must prevent the bulk rent/late-fee
/// run from posting a second charge for the same tenant+period.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PeriodChargeGuardTests(PostgresFixture fixture)
{
    private static readonly RunPeriod Period = new(2026, 7); // clean period, no demo seed activity

    [Fact]
    public async Task Rent_run_marks_AlreadyDone_when_manual_RentCharged_exists_for_tenant_in_period()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);

        // Post a manual RentCharged using a non-bulk source_ref (simulates M3 composer or import).
        await DispatchAsync(ctx.OrgId, async (s, _) =>
        {
            await s.Send(new AddCharge(
                ctx.TenantId, ctx.Rent,
                new DateOnly(Period.Year, Period.Month, 1),
                "rent", null,
                $"manual:rent:tenant={ctx.TenantId}"), // non-bulk key
                ct);
        }, ct);

        // Preview: the lease must be AlreadyDone (structural period guard detects the manual charge).
        RunPreview? preview = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            preview = await engine.PreviewAsync(RunType.Rent, Period, ct);
        }, ct);

        preview.ShouldNotBeNull();
        preview!.Rows.Count.ShouldBe(1, "one active lease in the test org");
        preview.Rows[0].AlreadyDone.ShouldBeTrue(
            "structural period guard must flag lease AlreadyDone when a manual RentCharged exists in the period");

        // Confirm with the lease selected — must be Skipped, no second RentCharged posted.
        RunResult? result = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            result = await engine.ConfirmAsync(RunType.Rent, Period, [ctx.LeaseId], ct);
        }, ct);

        result.ShouldNotBeNull();
        result!.Posted.ShouldBe(0, "no second charge must be posted — guard prevents double-charge");
        result.Skipped.ShouldBe(1, "already-charged lease must be Skipped, not double-charged");

        // DB-level proof: exactly 1 RentCharged entry exists for the tenant in the period.
        // (Guard prevented the confirm from posting a second one.)
        int rentChargedCount = 0;
        await DispatchAsync(ctx.OrgId, async (_, sp) =>
        {
            var db = sp.GetRequiredService<DbContext>();
            var rows = await db.Database.SqlQuery<CountRow>(
                $"""
                SELECT COUNT(DISTINCT je.id)::int AS "Value"
                FROM journal_entries je
                JOIN journal_lines jl ON jl.entry_id = je.id
                WHERE je.event_type = 'RentCharged'
                  AND je.entry_date >= {new DateOnly(Period.Year, Period.Month, 1)}
                  AND je.entry_date <= {new DateOnly(Period.Year, Period.Month, DateTime.DaysInMonth(Period.Year, Period.Month))}
                  AND jl.tenant_id = {ctx.TenantId}
                """).ToListAsync(ct);
            rentChargedCount = rows.Single().Value;
        }, ct);

        rentChargedCount.ShouldBe(1, "exactly one RentCharged entry must exist — the guard blocked a double-charge");
    }

    [Fact]
    public async Task Late_fee_run_marks_AlreadyDone_when_manual_FeeCharged_Late_exists_for_tenant_in_period()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);

        // Make the tenant delinquent: post rent charge without payment → balance outstanding.
        await DispatchAsync(ctx.OrgId, async (s, _) =>
        {
            await s.Send(new AddCharge(
                ctx.TenantId, ctx.Rent,
                new DateOnly(Period.Year, Period.Month, 1),
                "rent", null,
                $"rent:{Period.Key}:lease={ctx.LeaseId}"),
                ct);
        }, ct);

        // Set org settings: flat late fee $50, 0 grace days → tenant immediately eligible.
        await DispatchAsync(ctx.OrgId, async (s, _) =>
        {
            await s.Send(new UpdateOrgSettings(
                null, null, null, null, null, null, null, null, null,
                LateFeeGraceDays: 0,
                LateFeeKind: "flat",
                LateFeeAmount: 50m),
                ct);
        }, ct);

        // Post a manual FeeCharged/Late (non-bulk source_ref — simulates M3 composer).
        await DispatchAsync(ctx.OrgId, async (s, _) =>
        {
            await s.Send(new AddCharge(
                ctx.TenantId, 50m,
                new DateOnly(Period.Year, Period.Month, 31),
                "late", null,
                $"manual:latefee:tenant={ctx.TenantId}"), // non-bulk key
                ct);
        }, ct);

        // Preview: the lease must be AlreadyDone (structural period guard detects the manual late fee).
        RunPreview? preview = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            preview = await engine.PreviewAsync(RunType.LateFee, Period, ct);
        }, ct);

        preview.ShouldNotBeNull();
        // If the lease appears in the preview (it's delinquent), AlreadyDone must be true.
        if (preview!.Rows.Count > 0)
        {
            preview.Rows[0].AlreadyDone.ShouldBeTrue(
                "structural period guard must flag lease AlreadyDone when a manual FeeCharged/Late exists");
        }

        // Confirm with the lease — must not post a second late fee.
        RunResult? confirmResult = null;
        await RunAsync(ctx.OrgId, async (engine, _) =>
        {
            confirmResult = await engine.ConfirmAsync(RunType.LateFee, Period, [ctx.LeaseId], ct);
        }, ct);

        confirmResult.ShouldNotBeNull();
        confirmResult!.Posted.ShouldBe(0, "structural guard must prevent a second late fee from being posted");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record CountRow(int Value);

    private sealed record Ctx(Guid OrgId, Guid TrustBankId, Guid OwnerId, Guid TenantId, Guid LeaseId, decimal Rent);

    private async Task<Ctx> SetupAsync(CancellationToken ct)
    {
        var orgId = await NewOrgAsync(ct);
        Guid trustBankId = default, ownerId = default, tenantId = default, leaseId = default;
        const decimal rent = 1200m;

        await DispatchAsync(orgId, async (s, _) =>
        {
            var trust = await s.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct);
            trustBankId = trust.Id;

            ownerId = await s.Send(new CreateOwner("Guard Test Owner", null, null, null, null, 0m), ct);
            var propId = await s.Send(new CreateProperty(ownerId, "1 Guard St", "Raleigh", "NC", null, null), ct);
            var unitId = await s.Send(new CreateUnit(propId, "#1", rent, "occupied"), ct);
            tenantId = await s.Send(new CreateTenant("Guard Tenant", null, null, "current"), ct);
            leaseId = await s.Send(new CreateLease(
                tenantId, unitId,
                new DateOnly(2025, 1, 1), new DateOnly(2027, 12, 31),
                rent, rent, "active"), ct);
        }, ct);

        return new Ctx(orgId, trustBankId, ownerId, tenantId, leaseId, rent);
    }

    private async Task<Guid> NewOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Guard Test Org {orgId:N}" });
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
