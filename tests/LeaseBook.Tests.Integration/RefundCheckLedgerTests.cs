using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Refunds;
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
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #473: the Accounting half of a refund check. The refund draws on the bank that holds the liability
/// (never a caller-chosen bank, #308), keeps a deposit's collection owner/property (ADR-026), writes the
/// check number into the posted description, and is voidable only through the guarded refund-check void
/// while its bank line is still outstanding. Each test runs in its own org so golden figures stay put.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RefundCheckLedgerTests(PostgresFixture fixture)
{
    private static readonly DateOnly Feb1 = new(2026, 2, 1);
    private static readonly DateOnly Mar2 = new(2026, 3, 2);

    [Fact]
    public async Task A_deposit_refund_draws_on_the_deposit_bank_and_keeps_the_collection_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectDeposit(ctx.TenantId, 1450m, Feb1, ctx.DepositBankId, null, Key()), c), ct);

        var checkId = UuidV7.NewId();
        var posted = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new PostRefundCheck(ctx.TenantId, "deposit", 1450m, Mar2, checkId, 1043), c), ct);

        posted.BankAccountId.ShouldBe(ctx.DepositBankId);
        var entry = await ReadEntryAsync(ctx.OrgId, posted.EntryId, ct);
        entry.EventType.ShouldBe("RefundIssued");
        entry.Description.ShouldBe("Refund check #1043 — security deposit");
        entry.SourceRef.ShouldBe($"refund-check:{checkId}");
        entry.Lines.ShouldContain(l => l.AccountClass == AccountClass.DepositLiability && l.Debit == 1450m
            && l.OwnerId == ctx.OwnerId && l.PropertyId == ctx.PropertyId && l.BankAccountId == ctx.DepositBankId);
        entry.Lines.ShouldContain(l => l.AccountClass == AccountClass.TrustBank && l.Credit == 1450m
            && l.BankAccountId == ctx.DepositBankId);

        var balances = await DispatchAsync(ctx.OrgId, (s, c) => s.Query(new GetRefundableBalances([ctx.TenantId]), c), ct);
        balances.ShouldBeEmpty();
        await AssertTrustEquationAsync(ctx.OrgId, ct);
    }

    [Fact]
    public async Task A_prepayment_refund_draws_on_the_bank_holding_the_prepayment()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectPrepayment(ctx.TenantId, 75m, Feb1, ctx.TrustBankId, null, Key()), c), ct);

        var posted = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new PostRefundCheck(ctx.TenantId, "prepayment", 75m, Mar2, UuidV7.NewId(), 2001), c), ct);

        posted.BankAccountId.ShouldBe(ctx.TrustBankId);
        var entry = await ReadEntryAsync(ctx.OrgId, posted.EntryId, ct);
        entry.Description.ShouldBe("Refund check #2001 — prepaid credit");
        entry.Lines.ShouldContain(l => l.AccountClass == AccountClass.DepositLiability && l.Debit == 75m
            && l.OwnerId == null && l.BankAccountId == ctx.TrustBankId);
        await AssertTrustEquationAsync(ctx.OrgId, ct);
    }

    [Fact]
    public async Task Refundable_balances_list_each_held_bucket_with_its_bank()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectDeposit(ctx.TenantId, 1450m, Feb1, ctx.DepositBankId, null, Key()), c), ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectPrepayment(ctx.TenantId, 75m, Feb1, ctx.TrustBankId, null, Key()), c), ct);

        var balances = await DispatchAsync(ctx.OrgId, (s, c) => s.Query(new GetRefundableBalances([ctx.TenantId]), c), ct);

        balances.Count.ShouldBe(2);
        balances.ShouldContain(b => b.TenantId == ctx.TenantId && b.Source == "deposit" && b.Held == 1450m
            && b.BankAccountId == ctx.DepositBankId && b.OwnerId == ctx.OwnerId && b.PropertyId == ctx.PropertyId);
        balances.ShouldContain(b => b.TenantId == ctx.TenantId && b.Source == "prepayment" && b.Held == 75m
            && b.BankAccountId == ctx.TrustBankId && b.OwnerId == null && b.PropertyId == null);
    }

    [Fact]
    public async Task A_refund_spanning_two_held_buckets_must_name_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectPrepayment(ctx.TenantId, 75m, Feb1, ctx.TrustBankId, null, Key()), c), ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectPrepayment(ctx.TenantId, 40m, Feb1, ctx.DepositBankId, null, Key()), c), ct);

        await Should.ThrowAsync<RefundBucketAmbiguousException>(() => DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new PostRefundCheck(ctx.TenantId, "prepayment", 40m, Mar2, UuidV7.NewId(), 2001), c), ct));

        var posted = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new PostRefundCheck(ctx.TenantId, "prepayment", 40m, Mar2, UuidV7.NewId(), 2001,
                new RefundBucket(ctx.DepositBankId, null, null)), c), ct);
        posted.BankAccountId.ShouldBe(ctx.DepositBankId);
    }

    [Fact]
    public async Task A_refund_cannot_draw_more_than_its_bucket_holds_even_when_the_tenant_total_covers_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectPrepayment(ctx.TenantId, 75m, Feb1, ctx.TrustBankId, null, Key()), c), ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectPrepayment(ctx.TenantId, 40m, Feb1, ctx.DepositBankId, null, Key()), c), ct);

        // 100 ≤ 115 held across both banks, but only 40 sits in the deposit bank.
        await Should.ThrowAsync<InsufficientLiabilityException>(() => DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new PostRefundCheck(ctx.TenantId, "prepayment", 100m, Mar2, UuidV7.NewId(), 2001,
                new RefundBucket(ctx.DepositBankId, null, null)), c), ct));
    }

    [Fact]
    public async Task A_bucket_that_holds_nothing_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectDeposit(ctx.TenantId, 1450m, Feb1, ctx.DepositBankId, null, Key()), c), ct);

        await Should.ThrowAsync<InsufficientLiabilityException>(() => DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new PostRefundCheck(ctx.TenantId, "deposit", 100m, Mar2, UuidV7.NewId(), 1043,
                new RefundBucket(ctx.TrustBankId, ctx.PropertyId, ctx.OwnerId)), c), ct));
        await Should.ThrowAsync<InsufficientLiabilityException>(() => DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new PostRefundCheck(ctx.TenantId, "prepayment", 10m, Mar2, UuidV7.NewId(), 1043), c), ct));
    }

    [Fact]
    public async Task Voiding_an_outstanding_refund_check_restores_the_liability_and_clears_both_bank_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectDeposit(ctx.TenantId, 1450m, Feb1, ctx.DepositBankId, null, Key()), c), ct);
        var posted = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new PostRefundCheck(ctx.TenantId, "deposit", 1450m, Mar2, UuidV7.NewId(), 1043), c), ct);

        var voided = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new VoidRefundCheck(posted.EntryId, "Misprinted", Mar2), c), ct);

        var balances = await DispatchAsync(ctx.OrgId, (s, c) => s.Query(new GetRefundableBalances([ctx.TenantId]), c), ct);
        balances.ShouldHaveSingleItem().Held.ShouldBe(1450m);

        var statuses = await DispatchAsync(ctx.OrgId, (s, c) => s.Query(new GetRefundCheckStatuses([posted.EntryId]), c), ct);
        statuses[posted.EntryId].ShouldBe(new RefundCheckStatus("voided", voided.EntryId));

        var register = await DispatchAsync(ctx.OrgId, (s, c) => s.Query(new GetBankRegister(ctx.DepositBankId), c), ct);
        register.Rows.Where(r => r.Status == BankLineStatus.Uncleared).Select(r => r.Withdrawal ?? -r.Deposit).ShouldBe([-1450m]);
        await AssertTrustEquationAsync(ctx.OrgId, ct);
    }

    [Fact]
    public async Task A_refund_check_status_follows_its_bank_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectDeposit(ctx.TenantId, 1450m, Feb1, ctx.DepositBankId, null, Key()), c), ct);
        var posted = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new PostRefundCheck(ctx.TenantId, "deposit", 1450m, Mar2, UuidV7.NewId(), 1043), c), ct);

        var before = await DispatchAsync(ctx.OrgId, (s, c) => s.Query(new GetRefundCheckStatuses([posted.EntryId]), c), ct);
        before[posted.EntryId].ShouldBe(new RefundCheckStatus("outstanding", null));

        await ClearBankLineAsync(ctx.OrgId, posted.EntryId, ct);

        var after = await DispatchAsync(ctx.OrgId, (s, c) => s.Query(new GetRefundCheckStatuses([posted.EntryId]), c), ct);
        after[posted.EntryId].ShouldBe(new RefundCheckStatus("cleared", null));
    }

    [Fact]
    public async Task A_cleared_refund_check_cannot_be_voided()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectDeposit(ctx.TenantId, 1450m, Feb1, ctx.DepositBankId, null, Key()), c), ct);
        var posted = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new PostRefundCheck(ctx.TenantId, "deposit", 1450m, Mar2, UuidV7.NewId(), 1043), c), ct);
        await ClearBankLineAsync(ctx.OrgId, posted.EntryId, ct);

        await Should.ThrowAsync<RefundCheckClearedException>(() => DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new VoidRefundCheck(posted.EntryId, "Lost", Mar2), c), ct));
    }

    [Fact]
    public async Task The_generic_void_refuses_a_refund_check()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectDeposit(ctx.TenantId, 1450m, Feb1, ctx.DepositBankId, null, Key()), c), ct);
        var posted = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new PostRefundCheck(ctx.TenantId, "deposit", 1450m, Mar2, UuidV7.NewId(), 1043), c), ct);

        await Should.ThrowAsync<RefundCheckVoidRequiredException>(() => DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new VoidEntry(posted.EntryId, "entered in error", Mar2, Key()), c), ct));
    }

    [Fact]
    public async Task The_refund_check_void_refuses_an_entry_that_is_not_a_refund_check()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        var deposit = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectDeposit(ctx.TenantId, 1450m, Feb1, ctx.DepositBankId, null, Key()), c), ct);

        await Should.ThrowAsync<EntryNotFoundException>(() => DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new VoidRefundCheck(deposit.EntryId, "wrong entry", Mar2), c), ct));
    }

    private sealed record Ctx(Guid OrgId, Guid OwnerId, Guid PropertyId, Guid TenantId, Guid TrustBankId, Guid DepositBankId);

    private sealed record LineView(AccountClass AccountClass, decimal? Debit, decimal? Credit, Guid? OwnerId,
        Guid? PropertyId, Guid? BankAccountId);

    private sealed record EntryView(string EventType, string? Description, string? SourceRef, IReadOnlyList<LineView> Lines);

    private static string Key() => UuidV7.NewId().ToString();

    private async Task<EntryView> ReadEntryAsync(Guid orgId, Guid entryId, CancellationToken ct)
    {
        EntryView view = null!;
        await DispatchScopeAsync(orgId, async (_, services) =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var entry = await db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == entryId, ct);
            var lines = await db.Set<JournalLine>().AsNoTracking().Where(l => l.EntryId == entryId)
                .Select(l => new LineView(l.AccountClass,
                    l.Debit == null ? null : l.Debit.Value.Amount,
                    l.Credit == null ? null : l.Credit.Value.Amount,
                    l.OwnerId, l.PropertyId, l.BankAccountId))
                .ToListAsync(ct);
            view = new EntryView(entry.EventType, entry.Description, entry.SourceRef, lines);
        }, ct);
        return view;
    }

    private async Task ClearBankLineAsync(Guid orgId, Guid entryId, CancellationToken ct)
    {
        await DispatchScopeAsync(orgId, async (sender, services) =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var lineId = await db.Set<JournalLine>().AsNoTracking()
                .Where(l => l.EntryId == entryId && l.AccountClass == AccountClass.TrustBank)
                .Select(l => l.Id).SingleAsync(ct);
            await sender.Send(new ApplyClearances([lineId]), ct);
        }, ct);
    }

    private async Task AssertTrustEquationAsync(Guid orgId, CancellationToken ct)
    {
        var equation = await DispatchAsync(orgId, (s, c) => s.Query(new GetTrustEquation(), c), ct);
        equation.Rows.ShouldAllBe(r => r.Variance == 0m);
    }

    private async Task<Ctx> SetupAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Refund Check Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        Ctx ctx = null!;
        await DispatchScopeAsync(orgId, async (s, _) =>
        {
            var ownerId = await s.Send(new CreateOwner("Owner", null, null, null, 800, 0m), ct);
            var propertyId = await s.Send(new CreateProperty(ownerId, "412 Oakmont Ave", "Asheville", "NC", "28801", null), ct);
            var unitId = await s.Send(new CreateUnit(propertyId, "#2B", 1450m, "available"), ct);
            var tenantId = await s.Send(new CreateTenant("Jasmine Carter", null, null, "current"), ct);
            await s.Send(new CreateLease(tenantId, unitId, new DateOnly(2025, 6, 1), new DateOnly(2026, 5, 31), 1450m, 1450m, "active"), ct);
            var trust = await s.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct);
            var deposit = await s.Send(new CreateBankAccount("Deposit Trust", null, null, "deposit"), ct);
            ctx = new Ctx(orgId, ownerId, propertyId, tenantId, trust.Id, deposit.Id);
        }, ct);
        return ctx;
    }

    private async Task DispatchScopeAsync(Guid orgId, Func<ISender, IServiceProvider, Task> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        await executor.RunAsSystemAsync(orgId, "test-harness",
            () => work(scope.ServiceProvider.GetRequiredService<ISender>(), scope.ServiceProvider), ct);
    }

    private async Task<T> DispatchAsync<T>(Guid orgId, Func<ISender, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        T result = default!;
        await DispatchScopeAsync(orgId, async (s, _) => result = await work(s, ct), ct);
        return result;
    }
}
