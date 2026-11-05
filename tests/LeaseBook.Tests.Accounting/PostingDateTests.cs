using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Refunds;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// Which day it is for the books (#642, ADR-037). "Today" is the date in the organization's time
/// zone, so at 21:00 Eastern on 31 October it is still 31 October, though the UTC date is already
/// 1 November. Money moving in or out cannot be dated after that day; a charge or a credit billed
/// ahead can.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PostingDateTests(PostgresFixture fixture)
{
    private readonly Guid _tenant = UuidV7.NewId();
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _owner2 = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();
    private readonly Guid _unit = UuidV7.NewId();

    // 21:00 EDT on 31 Oct 2026 is 01:00 UTC on 1 Nov.
    private static readonly StoppedClock EasternEvening = new(new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero));
    // 23:30 PDT on 31 Oct 2026 is 06:30 UTC on 1 Nov; in New York it is already 02:30 on 1 Nov.
    private static readonly StoppedClock PacificLateEvening = new(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero));
    private static readonly DateOnly Oct1 = new(2026, 10, 1);
    private static readonly DateOnly Oct31 = new(2026, 10, 31);
    private static readonly DateOnly Nov1 = new(2026, 11, 1);

    private static readonly Guid Any = UuidV7.NewId();

    public static TheoryData<string> GuardedCommands() => new(
        "RecordPayment", "CollectDeposit", "CollectPrepayment", "ApplyDeposit", "ApplyPrepayment",
        "TransferPropertyDeposits", "VoidEntry", "VoidRefundCheck", "PostRefundCheck", "RecordBankAdjustment");

    private Task Send(ISender sender, OrgScope scope, string command, DateOnly date, CancellationToken ct) => command switch
    {
        "RecordPayment" => sender.Send(new RecordPayment(_tenant, 100m, date, "check", scope.TrustBankId, null, "pay"), ct),
        "CollectDeposit" => sender.Send(new CollectDeposit(_tenant, 100m, date, scope.DepositBankId, null, "dep"), ct),
        "CollectPrepayment" => sender.Send(new CollectPrepayment(_tenant, 100m, date, scope.TrustBankId, null, "pre"), ct),
        "ApplyDeposit" => sender.Send(new ApplyDeposit(_tenant, 100m, date, scope.DepositBankId, scope.TrustBankId,
            "to-owner-income", "damages", "app-dep"), ct),
        "ApplyPrepayment" => sender.Send(new ApplyPrepayment(_tenant, 100m, date, scope.TrustBankId, null, "app-pre"), ct),
        "TransferPropertyDeposits" => sender.Send(new TransferPropertyDeposits(_property, _owner, _owner2, date, "xfer"), ct),
        "VoidEntry" => sender.Send(new VoidEntry(Any, "posted twice", date, "void"), ct),
        "VoidRefundCheck" => sender.Send(new VoidRefundCheck(Any, "printed in error", date), ct),
        "PostRefundCheck" => sender.Send(new PostRefundCheck(_tenant, "deposit", 100m, date, UuidV7.NewId(), 1001), ct),
        "RecordBankAdjustment" => sender.Send(new RecordBankAdjustment(
            "fee", 10m, date, scope.OperatingBankId, null, null, "fee"), ct),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    [Theory]
    [MemberData(nameof(GuardedCommands))]
    public async Task Money_in_or_out_dated_after_the_organizations_today_is_refused(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);

        PostingDateInFutureException refused = null!;
        await scope.RunAsync(async () => refused = await Should.ThrowAsync<PostingDateInFutureException>(
            () => Send(AccountingSender.Create(scope, Leases(), EasternEvening), scope, command, Nov1, ct)), ct);

        (refused.Code, refused.Date, refused.Today).ShouldBe(("posting_date_in_future", Nov1, Oct31));
        refused.Message.ShouldBe("The date 2026-11-01 is after today, 2026-10-31, in the organization's time zone.");
        await scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>().CountAsync(ct)).ShouldBe(0), ct);
    }

    [Fact]
    public async Task Money_dated_the_organizations_today_posts_though_the_utc_date_is_tomorrow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);

        await scope.RunAsync(async () =>
        {
            var sender = AccountingSender.Create(scope, Leases(), EasternEvening);
            await sender.Send(new AddCharge(_tenant, 500m, Oct1, "rent", null, "rent:oct"), ct);
            var payment = await sender.Send(new RecordPayment(_tenant, 100m, Oct31, "check", scope.TrustBankId, null, "pay"), ct);
            await sender.Send(new CollectDeposit(_tenant, 300m, Oct31, scope.DepositBankId, null, "dep"), ct);
            await sender.Send(new CollectPrepayment(_tenant, 200m, Oct31, scope.TrustBankId, null, "pre"), ct);
            await sender.Send(new ApplyPrepayment(_tenant, 50m, Oct31, scope.TrustBankId, null, "app-pre"), ct);
            await sender.Send(new ApplyDeposit(_tenant, 50m, Oct31, scope.DepositBankId, scope.TrustBankId,
                "against-charges", "damages", "app-dep"), ct);
            await sender.Send(new RecordBankAdjustment("interest", 1m, Oct31, scope.TrustBankId, null, null, "int"), ct);
            await sender.Send(new VoidEntry(payment.EntryId, "posted twice", Oct31, "void"), ct);
        }, ct);

        await scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>()
            .Where(e => e.EntryDate == Oct31).CountAsync(ct)).ShouldBe(7), ct);
    }

    [Fact]
    public async Task A_charge_or_a_credit_billed_ahead_is_not_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        var nextMonth = new DateOnly(2026, 12, 1);

        await scope.RunAsync(async () =>
        {
            var sender = AccountingSender.Create(scope, Leases(), EasternEvening);
            await sender.Send(new AddCharge(_tenant, 500m, nextMonth, "rent", null, "rent:dec"), ct);
            await sender.Send(new IssueCredit(_tenant, 25m, nextMonth, "goodwill", "credit:dec"), ct);
        }, ct);

        await scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>()
            .Where(e => e.EntryDate == nextMonth).CountAsync(ct)).ShouldBe(2), ct);
    }

    [Fact]
    public async Task A_void_with_no_date_is_dated_the_organizations_today_not_the_utc_date()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);

        PostResult reversal = null!;
        await scope.RunAsync(async () =>
        {
            var sender = AccountingSender.Create(scope, Leases(), EasternEvening);
            var charge = await sender.Send(new AddCharge(_tenant, 100m, Oct1, "rent", null, "rent:oct"), ct);
            reversal = await sender.Send(new VoidEntry(charge.EntryId, "posted twice", null, "void"), ct);
        }, ct);

        await scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>().AsNoTracking()
            .SingleAsync(e => e.Id == reversal.EntryId, ct)).EntryDate.ShouldBe(Oct31), ct);
    }

    [Fact]
    public async Task The_organizations_zone_decides_the_day_not_the_server_or_eastern_time()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);

        // At 23:30 Pacific it is 31 October in Los Angeles and 1 November in New York.
        await scope.RunAsync(async () =>
        {
            var pacific = AccountingSender.Create(scope, Leases(), PacificLateEvening, "America/Los_Angeles");
            await pacific.Send(new RecordPayment(_tenant, 100m, Oct31, "check", scope.TrustBankId, null, "pay:oct31"), ct);
            (await Should.ThrowAsync<PostingDateInFutureException>(() => pacific.Send(
                new RecordPayment(_tenant, 100m, Nov1, "check", scope.TrustBankId, null, "pay:nov1"), ct))).Today.ShouldBe(Oct31);

            var eastern = AccountingSender.Create(scope, Leases(), PacificLateEvening, "America/New_York");
            await eastern.Send(new RecordPayment(_tenant, 100m, Nov1, "check", scope.TrustBankId, null, "pay:nov1-east"), ct);
        }, ct);
    }

    [Fact]
    public async Task A_processor_receipt_keeps_the_banks_date_though_it_is_the_organizations_tomorrow()
    {
        // The processor's bank date is evidence, bounded by the processor's own rules (the UTC date);
        // in the evening it can be the organization's tomorrow, and it still posts on that date.
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);

        PostResult receipt = null!;
        await scope.RunAsync(async () => receipt = await AccountingSender.Create(scope, Leases(), EasternEvening)
            .Send(new RecordSettledReceipt(_tenant, 100m, Nov1, "ach", scope.TrustBankId, "Tenant payment", "settled:1"), ct), ct);

        await scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>().AsNoTracking()
            .SingleAsync(e => e.Id == receipt.EntryId, ct)).EntryDate.ShouldBe(Nov1), ct);
    }

    private Task<OrgScope> NewScope(CancellationToken ct) => ProvisionedScopeAsync(
        fixture, ct, owners: [_owner, _owner2], tenants: [_tenant], properties: [_property], units: [_unit]);

    private Leases Leases() => new(new Dictionary<Guid, TenantPostingDimensions>
    {
        [_tenant] = new(_owner, _property, _unit),
    });
}
