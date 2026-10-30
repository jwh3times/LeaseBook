using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// A property sale moves every security deposit the seller holds for it to the buyer, as of the
/// effective date (ADR-026): one entry that debits each held position under the seller and credits it
/// under the buyer, with no cash line. <c>PropertyOwnershipTransferTests</c> drives it from Directory
/// through the host; these pin the command on its own, through the real sender.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class TransferPropertyDepositsTests(PostgresFixture fixture)
{
    private readonly Guid _seller = UuidV7.NewId();
    private readonly Guid _buyer = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();
    private readonly Guid _tenantA = UuidV7.NewId();
    private readonly Guid _tenantB = UuidV7.NewId();
    private static readonly DateOnly Jan10 = new(2026, 1, 10);
    private static readonly DateOnly Feb1 = new(2026, 2, 1);

    [Fact]
    public async Task Each_held_position_moves_from_the_seller_to_the_buyer_in_one_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await PostAsync(scope, Collected(scope, _tenantA, 1000m, Jan10), ct);
        await PostAsync(scope, Collected(scope, _tenantB, 750m, Jan10), ct);

        var id = (await TransferAsync(scope, Feb1, ct)).ShouldNotBeNull();

        await scope.RunAsync(async () =>
        {
            var entry = await scope.Db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == id, ct);
            (entry.EventType, entry.Description, entry.SourceRef, entry.EntryDate)
                .ShouldBe(("DepositResponsibilityTransferred", "Property ownership transfer", "sale:1", Feb1));
        }, ct);
        (await ReadLinesAsync(scope, id, ct)).ShouldBe(
        [
            Held(scope, _tenantA, _seller, debit: 1000m), Held(scope, _tenantA, _buyer, credit: 1000m),
            Held(scope, _tenantB, _seller, debit: 750m), Held(scope, _tenantB, _buyer, credit: 750m),
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task With_nothing_held_for_the_property_nothing_posts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);

        (await TransferAsync(scope, Feb1, ct)).ShouldBeNull();

        await scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>().CountAsync(ct)).ShouldBe(0), ct);
    }

    [Fact]
    public async Task A_closed_effective_month_is_refused_even_with_nothing_held()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await scope.RunAsync(() => Periods(scope).CloseAsync(2026, 2, ct), ct);

        var refused = await Should.ThrowAsync<PeriodClosedException>(() => TransferAsync(scope, Feb1, ct));

        (refused.Year, refused.Month).ShouldBe((2026, 2));
    }

    [Fact]
    public async Task Seller_activity_after_the_effective_date_must_be_reversed_first()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await PostAsync(scope, Collected(scope, _tenantA, 1000m, Jan10), ct);
        await PostAsync(scope, Collected(scope, _tenantB, 200m, new DateOnly(2026, 2, 10)), ct);

        var refused = await Should.ThrowAsync<ValidationException>(() => TransferAsync(scope, Feb1, ct));

        refused.Errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe(
            "Later seller-attributed deposit activity must be reversed before recording this backdated ownership transfer.");
        await scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>().CountAsync(ct)).ShouldBe(2), ct);
    }

    [Fact]
    public async Task A_reconciled_month_on_a_bank_holding_a_moved_deposit_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await PostAsync(scope, Collected(scope, _tenantA, 1000m, Jan10), ct);
        // The transfer has no cash line, so the posting service would let it into a reconciled month.
        await scope.RunAsync(async () =>
        {
            var started = await new StartReconciliationHandler(scope.Db)
                .Handle(new StartReconciliation(scope.DepositBankId, 2026, 2, 0m), ct);
            await new FinalizeReconciliationHandler(scope.Db, scope.Actor).Handle(new FinalizeReconciliation(started.Id), ct);
        }, ct);

        var refused = await Should.ThrowAsync<AccountPeriodLockedException>(() => TransferAsync(scope, Feb1, ct));

        (refused.BankAccountId, refused.Year, refused.Month).ShouldBe((scope.DepositBankId, 2026, 2));
    }

    [Fact]
    public async Task The_positions_are_read_under_the_posting_lock_so_a_racing_application_is_not_moved_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await PostAsync(scope, Collected(scope, _tenantA, 1000m, Jan10), ct);

        // The first applies 400 of the seller's deposit; read before that commits, all 1,000 would move.
        var race = await Lanes.RaceAsync(fixture, scope,
            lane => lane.Events.PostAsync(new DepositApplied(_tenantA, _property, _seller, new Money(400m),
                new DateOnly(2026, 1, 20), scope.DepositBankId, scope.TrustBankId, DepositApplication.ToOwnerIncome,
                "damages"), ct),
            lane => lane.Sender(NoLeases).Send(Command(Feb1), ct), ct);

        race.SecondError.ShouldBeNull();
        (await ReadLinesAsync(scope, race.Second!.Value, ct)).ShouldBe(
            [Held(scope, _tenantA, _seller, debit: 600m), Held(scope, _tenantA, _buyer, credit: 600m)], ignoreOrder: true);
    }

    private static readonly Leases NoLeases = new(new Dictionary<Guid, TenantPostingDimensions>());

    private Task<OrgScope> NewScope(CancellationToken ct) => ProvisionedScopeAsync(
        fixture, ct, owners: [_seller, _buyer], tenants: [_tenantA, _tenantB], properties: [_property]);

    private TransferPropertyDeposits Command(DateOnly effective) => new(_property, _seller, _buyer, effective, "sale:1");

    private DepositCollected Collected(OrgScope scope, Guid tenant, decimal amount, DateOnly date) =>
        new(tenant, _property, _seller, new Money(amount), date, scope.DepositBankId, "deposit");

    private LineView Held(OrgScope scope, Guid tenant, Guid owner, decimal? debit = null, decimal? credit = null) =>
        new(AccountCodes.SecurityDepositsHeld, debit, credit, EntryBasis.Both, tenant, owner, _property, null, scope.DepositBankId);

    private async Task<Guid?> TransferAsync(OrgScope scope, DateOnly effective, CancellationToken ct)
    {
        Guid? id = null;
        await scope.RunAsync(async () => id = await AccountingSender.Create(scope, NoLeases).Send(Command(effective), ct), ct);
        return id;
    }

    private static async Task PostAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct) =>
        await scope.RunAsync(() => Events(scope).PostAsync(businessEvent, ct), ct);
}
