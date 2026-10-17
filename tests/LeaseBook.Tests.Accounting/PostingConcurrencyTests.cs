using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// Two postings at once (P31, M-E7). Each guarded event takes the organization's posting lock before
/// it reads the balance it posts against, so a second posting waits for the first to commit and reads
/// what it left; without the lock both read the same stale balance and the second over-draws it. The
/// unique indexes on source reference, reversal and assessment are the backstop when two postings
/// race past the service's own checks, and a collision there is answered with the same typed error a
/// sequential retry gets. Each race runs through <see cref="Lanes.RaceAsync{T1, T2}"/>: the first
/// posting is held open until the second has finished or is waiting on a lock, so the outcome never
/// depends on timing.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PostingConcurrencyTests(PostgresFixture fixture)
{
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();
    private readonly Guid _unit = UuidV7.NewId();
    private readonly Guid _tenant = UuidV7.NewId();

    [Fact]
    public async Task Two_payments_racing_for_one_receivable_clear_it_once_and_hold_the_rest_as_prepayment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        await PostAsync(scope, new RentCharged(_tenant, _property, _owner, _unit, new Money(1000m), D(1), "rent"), ct);

        var race = await Lanes.RaceAsync(fixture, scope,
            lane => lane.Events.PostAsync(Payment(scope, "first payment"), ct),
            lane => lane.Events.PostAsync(Payment(scope, "second payment"), ct), ct);

        race.SecondError.ShouldBeNull();
        // The second payment read the receivable the first had cleared, so all of it is a prepayment.
        (await ReadLinesAsync(scope, race.Second, ct)).ShouldBe(
        [
            new(AccountCodes.TrustBank(scope.TrustBankId), 1000m, null, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
            new(AccountCodes.TenantPrepayments, null, 1000m, EntryBasis.Both, _tenant, null, null, null, scope.TrustBankId),
        ], ignoreOrder: true);
        (await BalanceAsync(scope, b => b.TenantReceivableAsync(_tenant, ct), ct)).ShouldBe(0m);
        (await BalanceAsync(scope, b => b.PrepaymentsHeldAsync(_tenant, ct), ct)).ShouldBe(1000m);
    }

    [Fact]
    public async Task Two_deposit_applications_racing_for_one_held_deposit_cannot_both_draw_on_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        await PostAsync(scope, new DepositCollected(
            _tenant, _property, _owner, new Money(1000m), D(1), scope.DepositBankId, "deposit"), ct);

        var race = await Lanes.RaceAsync(fixture, scope,
            lane => lane.Events.PostAsync(DepositApplied(scope), ct),
            lane => lane.Events.PostAsync(DepositApplied(scope), ct), ct);

        var refused = race.SecondError.ShouldBeOfType<InsufficientLiabilityException>();
        (refused.Kind, refused.Requested, refused.Held).ShouldBe((LiabilityKind.Deposit, 600m, 400m));
        (await BalanceAsync(scope, b => b.DepositsHeldAsync(_tenant, ct), ct)).ShouldBe(400m);
    }

    [Fact]
    public async Task Two_prepayment_applications_racing_for_one_held_prepayment_cannot_both_draw_on_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        await PostAsync(scope, new RentCharged(_tenant, _property, _owner, _unit, new Money(2000m), D(1), "rent"), ct);
        await PostAsync(scope, new PrepaymentReceived(_tenant, _property, _owner, new Money(1000m), D(1), scope.TrustBankId, "prepay"), ct);

        var race = await Lanes.RaceAsync(fixture, scope,
            lane => lane.Events.PostAsync(PrepaymentApplied(scope), ct),
            lane => lane.Events.PostAsync(PrepaymentApplied(scope), ct), ct);

        var refused = race.SecondError.ShouldBeOfType<InsufficientLiabilityException>();
        (refused.Kind, refused.Requested, refused.Held).ShouldBe((LiabilityKind.Prepayment, 600m, 400m));
        (await BalanceAsync(scope, b => b.PrepaymentsHeldAsync(_tenant, ct), ct)).ShouldBe(400m);
    }

    [Fact]
    public async Task Two_fee_sweeps_racing_for_the_same_held_fees_cannot_both_take_them()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        await PostAsync(scope, new ManagementFeeAssessed(_owner, _property, new Money(300m), D(1), scope.TrustBankId, "fee"), ct);

        var race = await Lanes.RaceAsync(fixture, scope,
            lane => lane.Events.PostAsync(Sweep(scope), ct),
            lane => lane.Events.PostAsync(Sweep(scope), ct), ct);

        var refused = race.SecondError.ShouldBeOfType<InsufficientLiabilityException>();
        (refused.Kind, refused.Requested, refused.Held).ShouldBe((LiabilityKind.FeeSweep, 200m, 100m));
        (await BalanceAsync(scope, b => b.HeldFeesAsync(scope.TrustBankId, ct), ct)).ShouldBe(100m);
    }

    [Fact]
    public async Task Two_owner_disbursements_racing_for_the_same_equity_cannot_both_pay_out()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        await PostAsync(scope, new OwnerContribution(_owner, _property, new Money(1000m), D(1), scope.TrustBankId, "seed"), ct);

        var race = await Lanes.RaceAsync(fixture, scope,
            lane => lane.Events.PostAsync(new OwnerDisbursed(_owner, new Money(600m), D(5), scope.TrustBankId, "draw"), ct),
            lane => lane.Events.PostAsync(new OwnerDisbursed(_owner, new Money(600m), D(5), scope.TrustBankId, "draw"), ct), ct);

        var refused = race.SecondError.ShouldBeOfType<ReserveFloorException>();
        (refused.Amount, refused.Equity, refused.Reserve).ShouldBe((600m, 400m, 0m));
        (await BalanceAsync(scope, b => b.OwnerEquityCashAsync(_owner, ct), ct)).ShouldBe(400m);
    }

    [Fact]
    public async Task Two_vendor_payments_racing_for_the_same_equity_cannot_both_pay_out()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        await PostAsync(scope, new OwnerContribution(_owner, _property, new Money(1000m), D(1), scope.TrustBankId, "seed"), ct);

        var race = await Lanes.RaceAsync(fixture, scope,
            lane => lane.Events.PostAsync(VendorPaid(scope), ct),
            lane => lane.Events.PostAsync(VendorPaid(scope), ct), ct);

        var refused = race.SecondError.ShouldBeOfType<ReserveFloorException>();
        (refused.Amount, refused.Equity, refused.Reserve).ShouldBe((600m, 400m, 0m));
        (await BalanceAsync(scope, b => b.OwnerEquityCashAsync(_owner, ct), ct)).ShouldBe(400m);
    }

    [Fact]
    public async Task Two_refunds_racing_for_one_held_deposit_cannot_both_pay_it_back()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        await PostAsync(scope, new DepositCollected(
            _tenant, _property, _owner, new Money(1000m), D(1), scope.DepositBankId, "deposit"), ct);

        var race = await Lanes.RaceAsync(fixture, scope,
            lane => lane.Events.PostAsync(DepositRefund(scope), ct),
            lane => lane.Events.PostAsync(DepositRefund(scope), ct), ct);

        var refused = race.SecondError.ShouldBeOfType<InsufficientLiabilityException>();
        (refused.Kind, refused.Requested, refused.Held).ShouldBe((LiabilityKind.Refund, 600m, 400m));
        (await BalanceAsync(scope, b => b.DepositsHeldAsync(_tenant, ct), ct)).ShouldBe(400m);
    }

    [Fact]
    public async Task Two_processor_fee_shortfalls_racing_for_the_same_held_fees_cannot_both_take_them()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        await PostAsync(scope, new InterestEarned(new Money(100m), D(1), scope.TrustBankId, "held fees"), ct);

        var race = await Lanes.RaceAsync(fixture, scope,
            lane => lane.Events.PostAsync(Shortfall(scope), ct),
            lane => lane.Events.PostAsync(Shortfall(scope), ct), ct);

        var refused = race.SecondError.ShouldBeOfType<PmFeesInsufficientException>();
        (refused.Shortfall, refused.Held, refused.BankAccountId).ShouldBe((60m, 40m, scope.TrustBankId));
        (await BalanceAsync(scope, b => b.HeldFeesAsync(scope.TrustBankId, ct), ct)).ShouldBe(40m);
    }

    [Fact]
    public async Task Two_postings_racing_with_one_source_ref_leave_one_entry_and_name_it_to_the_loser()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        // Another entry with a reference of its own, which the loser must not be pointed at.
        await scope.RunAsync(() => Posting(scope).PostAsync(Rent("rent:jan"), ct), ct);

        var race = await Lanes.RaceAsync(fixture, scope,
            lane => lane.Posting.PostAsync(Rent("rent:feb"), ct),
            lane => lane.Posting.PostAsync(Rent("rent:feb"), ct), ct);

        // The loser passed the pre-check before the winner committed and collided at the unique index.
        var refused = race.SecondError.ShouldBeOfType<DuplicateSourceRefException>();
        (refused.SourceRef, refused.ExistingEntryId).ShouldBe(("rent:feb", race.First));
        (await CountAsync(scope, e => e.SourceRef == "rent:feb", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_posting_that_lost_a_source_ref_race_leaves_its_unit_of_work_usable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        // February's period already exists, so the loser's first collision is the source ref's, not the period's.
        await scope.RunAsync(() => Posting(scope).PostAsync(Rent("rent:jan"), ct), ct);

        // A caller that treats the duplicate as already done and goes on in the same transaction, as a
        // bulk run does: the losing entry must not still be waiting to be saved with the next one.
        var race = await Lanes.RaceAsync(fixture, scope,
            lane => lane.Posting.PostAsync(Rent("rent:feb"), ct),
            async lane =>
            {
                await Should.ThrowAsync<DuplicateSourceRefException>(() => lane.Posting.PostAsync(Rent("rent:feb"), ct));
                return await lane.Posting.PostAsync(Rent("rent:mar"), ct);
            }, ct);

        race.SecondError.ShouldBeNull();
        (await CountAsync(scope, e => e.SourceRef == "rent:feb", ct)).ShouldBe(1);
        (await CountAsync(scope, e => e.SourceRef == "rent:mar" && e.Id == race.Second, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Two_reversals_racing_for_one_entry_reverse_it_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        Guid original = default;
        await scope.RunAsync(async () => original = await Posting(scope).PostAsync(Rent("rent:feb"), ct), ct);

        var race = await Lanes.RaceAsync(fixture, scope,
            lane => lane.Reversal.ReverseAsync(original, "entered twice", D(10), ct),
            lane => lane.Reversal.ReverseAsync(original, "entered twice", D(10), ct), ct);

        var refused = race.SecondError.ShouldBeOfType<AlreadyReversedException>();
        (refused.EntryId, refused.Reason).ShouldBe((original, AlreadyReversedReason.AlreadyReversed));
        (await CountAsync(scope, e => e.ReversesEntryId == original, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Two_late_fees_racing_for_one_rent_charge_assess_it_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        var rent = await PostAsync(scope, new RentCharged(_tenant, _property, _owner, _unit, new Money(1000m), D(1), "rent"), ct);

        var race = await Lanes.RaceAsync(fixture, scope,
            lane => lane.Events.PostAsync(LateFee(rent, "late:a"), ct),
            lane => lane.Events.PostAsync(LateFee(rent, "late:b"), ct), ct);

        var refused = race.SecondError.ShouldBeOfType<RentObligationAlreadyAssessedException>();
        (refused.RentObligationEntryId, refused.ExistingEntryId).ShouldBe((rent, race.First));
        (await CountAsync(scope, e => e.AssessesEntryId == rent, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task The_posting_lock_cannot_be_taken_outside_an_organization_context()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        // Its key is the organization's, so with none there is nothing to serialize on.
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            new PostingLock(scope.Db, scope.Tenant).AcquireAsync(ct));

        ex.Message.ShouldBe("AcquireAsync requires an ambient organization context — the advisory lock key is per org.");
    }

    private Task<OrgScope> ScopeAsync(CancellationToken ct) =>
        ProvisionedScopeAsync(fixture, ct, owners: [_owner], tenants: [_tenant], properties: [_property], units: [_unit]);

    private static async Task<Guid> PostAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private static async Task<decimal> BalanceAsync(OrgScope scope, Func<BalanceReader, Task<decimal>> read, CancellationToken ct)
    {
        decimal balance = 0;
        await scope.RunAsync(async () => balance = await read(new BalanceReader(scope.Db)), ct);
        return balance;
    }

    private static async Task<int> CountAsync(
        OrgScope scope, System.Linq.Expressions.Expression<Func<JournalEntry, bool>> where, CancellationToken ct)
    {
        var count = 0;
        await scope.RunAsync(async () => count = await scope.Db.Set<JournalEntry>().AsNoTracking().CountAsync(where, ct), ct);
        return count;
    }

    private PaymentReceived Payment(OrgScope scope, string description) =>
        new(_tenant, _property, _owner, new Money(1000m), D(5), PaymentMethod.Ach, scope.TrustBankId, description);

    private DepositApplied DepositApplied(OrgScope scope) =>
        new(_tenant, _property, _owner, new Money(600m), D(5), scope.DepositBankId, scope.TrustBankId,
            DepositApplication.ToOwnerIncome, "damages");

    private PrepaymentApplied PrepaymentApplied(OrgScope scope) =>
        new(_tenant, _property, _owner, new Money(600m), D(5), scope.TrustBankId, "apply");

    private static PMFeesSwept Sweep(OrgScope scope) =>
        new(new Money(200m), D(5), scope.TrustBankId, scope.OperatingBankId, "sweep");

    private VendorPaid VendorPaid(OrgScope scope) =>
        new(_owner, _property, new Money(600m), D(5), scope.TrustBankId, "Acme Plumbing", "repair");

    private RefundIssued DepositRefund(OrgScope scope) =>
        new(_tenant, new Money(600m), D(5), scope.DepositBankId, RefundSource.Deposits, "refund",
            PropertyId: _property, OwnerId: _owner);

    private static ProcessorFeeDifference Shortfall(OrgScope scope) =>
        new(new Money(60m), FeeDifferenceDirection.Shortfall, D(5), scope.TrustBankId, "processor kept more");

    private FeeCharged LateFee(Guid rent, string sourceRef) =>
        new(_tenant, _property, _owner, _unit, new Money(50m), D(6), FeeKind.Late, "late fee", sourceRef, AssessesEntryId: rent);

    private PostEntryRequest Rent(string sourceRef) => new(
        D(1), "RentCharged", null, "rent", sourceRef,
        [
            new PostLineRequest(AccountCodes.TenantReceivable, new Money(1000m), null, EntryBasis.Accrual,
                PropertyId: _property, OwnerId: _owner, TenantId: _tenant),
            new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(1000m), EntryBasis.Accrual,
                PropertyId: _property, OwnerId: _owner),
        ]);

    private static DateOnly D(int day) => new(2026, 2, day);
}
