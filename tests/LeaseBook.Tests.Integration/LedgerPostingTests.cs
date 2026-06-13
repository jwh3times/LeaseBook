using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Features.BankAccounts;
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
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-01: the ledger write-command surface dispatched through the real CQRS pipeline (host DI, app
/// role, org transaction). Each command resolves dimensions via the <c>ITenantPostingDimensions</c>
/// adapter (P58), posts through the existing engine (M3-E1), and the ledger reflects it; the trust
/// equation stays $0.00; idempotency dedups on <c>sourceRef</c> (P54); the receivable guard (P51) blocks
/// over-application; and a post that can't resolve a lease (no lease / another org's tenant) is rejected.
/// Each test runs in its own seeded org so the demo org's golden figures stay byte-stable (M3-E9).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class LedgerPostingTests(PostgresFixture fixture)
{
    private static readonly DateOnly Feb1 = new(2026, 2, 1);
    private static readonly DateOnly Feb3 = new(2026, 2, 3);

    [Fact]
    public async Task Record_payment_posts_and_the_ledger_reflects_it_and_the_trust_equation_holds()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);

        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new AddCharge(ctx.TenantId, 1450m, Feb1, "rent", null, Key()), c), ct);
        var posted = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new RecordPayment(ctx.TenantId, 1450m, Feb3, "ach", ctx.TrustBankId, null, Key()), c), ct);

        var ledger = await DispatchAsync(ctx.OrgId, (s, c) => s.Query(new GetTenantLedger(ctx.TenantId), c), ct);
        ledger.Rows.Count.ShouldBe(2);
        ledger.Rows.ShouldContain(r => r.EntryId == posted.EntryId && r.Category == "Payment" && r.Payment == 1450m);
        ledger.Balance.ShouldBe(0m);

        var equation = await DispatchAsync(ctx.OrgId, (s, c) => s.Query(new GetTrustEquation(), c), ct);
        equation.Rows.ShouldAllBe(r => r.Variance == 0m);
    }

    [Fact]
    public async Task Add_charge_posts_rent_and_a_fee_with_their_ledger_categories()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);

        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(new AddCharge(ctx.TenantId, 1450m, Feb1, "rent", null, Key()), c), ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(new AddCharge(ctx.TenantId, 50m, Feb3, "late", "Late fee", Key()), c), ct);

        var ledger = await DispatchAsync(ctx.OrgId, (s, c) => s.Query(new GetTenantLedger(ctx.TenantId), c), ct);
        ledger.Rows.Select(r => r.Category).ShouldBe(["Rent", "Late Fee"]);
        ledger.Balance.ShouldBe(1500m);
    }

    [Fact]
    public async Task Duplicate_source_ref_is_rejected_with_the_existing_entry_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        var key = Key();

        var first = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new AddCharge(ctx.TenantId, 1450m, Feb1, "rent", null, key), c), ct);

        var ex = await Should.ThrowAsync<DuplicateSourceRefException>(() => DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new AddCharge(ctx.TenantId, 1450m, Feb1, "rent", null, key), c), ct));
        ex.ExistingEntryId.ShouldBe(first.EntryId);
    }

    [Fact]
    public async Task Voiding_an_entry_posts_a_linked_reversal_that_nets_the_ledger_to_baseline()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);

        var charge = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new AddCharge(ctx.TenantId, 1450m, Feb1, "rent", null, Key()), c), ct);
        var reversal = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new VoidEntry(charge.EntryId, "entered in error", Feb3, Key()), c), ct);

        var ledger = await DispatchAsync(ctx.OrgId, (s, c) => s.Query(new GetTenantLedger(ctx.TenantId), c), ct);
        ledger.Rows.ShouldContain(r => r.EntryId == charge.EntryId && r.IsVoided);
        ledger.Rows.ShouldContain(r => r.EntryId == reversal.EntryId && r.ReversesEntryId == charge.EntryId);
        ledger.Balance.ShouldBe(0m);
    }

    [Fact]
    public async Task Deposit_collect_then_apply_against_charges_reflects_and_over_apply_is_blocked()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);

        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(new AddCharge(ctx.TenantId, 1000m, Feb1, "rent", null, Key()), c), ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectDeposit(ctx.TenantId, 1500m, Feb1, ctx.DepositBankId, null, Key()), c), ct);

        // 1200 ≤ 1500 held but > 1000 owed → blocked.
        await Should.ThrowAsync<InsufficientReceivableException>(() => DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new ApplyDeposit(ctx.TenantId, 1200m, Feb3, ctx.DepositBankId, ctx.TrustBankId, "against-charges", "move-out", Key()), c), ct));

        // Applying exactly the receivable clears it.
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new ApplyDeposit(ctx.TenantId, 1000m, Feb3, ctx.DepositBankId, ctx.TrustBankId, "against-charges", "move-out", Key()), c), ct);

        var ledger = await DispatchAsync(ctx.OrgId, (s, c) => s.Query(new GetTenantLedger(ctx.TenantId), c), ct);
        ledger.Balance.ShouldBe(0m);
        var equation = await DispatchAsync(ctx.OrgId, (s, c) => s.Query(new GetTrustEquation(), c), ct);
        equation.Rows.ShouldAllBe(r => r.Variance == 0m);
    }

    [Fact]
    public async Task A_post_for_a_tenant_with_no_active_lease_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        Guid tenantId = default, trustBankId = default;
        await DispatchScopeAsync(orgId, async (s, _) =>
        {
            tenantId = await s.Send(new CreateTenant("Leaseless Larry", null, null, "current"), ct);
            trustBankId = (await s.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct)).Id;
        }, ct);

        await Should.ThrowAsync<ValidationException>(() => DispatchAsync(orgId, (s, c) => s.Send(
            new RecordPayment(tenantId, 100m, Feb1, "ach", trustBankId, null, Key()), c), ct));
    }

    [Fact]
    public async Task A_post_cannot_target_another_orgs_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = await SetupAsync(ct);
        var orgB = await SetupAsync(ct);

        // Org B posts onto org A's tenant: RLS hides the tenant/lease, so the dimension port returns
        // null and the command rejects — no cross-org mis-attribution.
        await Should.ThrowAsync<ValidationException>(() => DispatchAsync(orgB.OrgId, (s, c) => s.Send(
            new RecordPayment(orgA.TenantId, 100m, Feb1, "ach", orgB.TrustBankId, null, Key()), c), ct));
    }

    private sealed record Ctx(Guid OrgId, Guid OwnerId, Guid TenantId, Guid TrustBankId, Guid DepositBankId);

    private static string Key() => UuidV7.NewId().ToString();

    private async Task<Ctx> SetupAsync(CancellationToken ct)
    {
        var orgId = await NewOrgAsync(ct);
        Ctx ctx = null!;
        await DispatchScopeAsync(orgId, async (s, _) =>
        {
            var ownerId = await s.Send(new CreateOwner("Owner", null, null, null, 800, 0m), ct);
            var propertyId = await s.Send(new CreateProperty(ownerId, "412 Oakmont Ave", "Asheville", "NC", "28801", null), ct);
            var unitId = await s.Send(new CreateUnit(propertyId, "#2B", 1450m, "occupied"), ct);
            var tenantId = await s.Send(new CreateTenant("Jasmine Carter", null, null, "current"), ct);
            await s.Send(new CreateLease(tenantId, unitId, new DateOnly(2025, 6, 1), new DateOnly(2026, 5, 31), 1450m, 1450m, "active"), ct);
            var trust = await s.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct);
            var deposit = await s.Send(new CreateBankAccount("Deposit Trust", null, null, "deposit"), ct);
            ctx = new Ctx(orgId, ownerId, tenantId, trust.Id, deposit.Id);
        }, ct);
        return ctx;
    }

    private async Task<Guid> NewOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Ledger Posting Org {orgId:N}" });
        await migratorDb.SaveChangesAsync(ct);
        return orgId;
    }

    private async Task DispatchScopeAsync(Guid orgId, Func<ISender, IServiceProvider, Task> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        await executor.RunAsync(orgId, () => work(scope.ServiceProvider.GetRequiredService<ISender>(), scope.ServiceProvider), ct);
    }

    private async Task<T> DispatchAsync<T>(Guid orgId, Func<ISender, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        T result = default!;
        await DispatchScopeAsync(orgId, async (s, _) => result = await work(s, ct), ct);
        return result;
    }
}
