using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.Modules.Accounting.Features.Refunds;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// The Accounting half of a refund check (#473), sent through the real dispatcher so each validator
/// runs in front of its handler. What is pinned: the bucket a check draws on and the text it posts,
/// the refundable balances the form is offered, the status each check reports, and the guarded void
/// — which entries it accepts, what it refuses, what it posts and clears, and the lock it takes so a
/// second void is answered as a void. The integration suite drives the same paths over HTTP and the
/// host's adapters; these run under mutation testing.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RefundCheckTests(PostgresFixture fixture)
{
    private readonly Guid _tenant = UuidV7.NewId();
    private readonly Guid _other = UuidV7.NewId();
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();
    private readonly Guid _unit = UuidV7.NewId();
    private static readonly DateOnly Feb1 = new(2026, 2, 1);
    private static readonly DateOnly Mar2 = new(2026, 3, 2);
    private static readonly DateOnly Mar5 = new(2026, 3, 5);

    // ---- validation: each rule refuses its own field, and nothing else does ----

    private static readonly Guid Some = UuidV7.NewId();
    private static readonly Guid Other = UuidV7.NewId();
    private const string AmountMessage = "Amount must be a positive value with at most 2 decimal places.";

    private static readonly PostRefundCheck Check = new(Some, "deposit", 100m, Feb1, Other, 1001);
    private static readonly VoidRefundCheck Void = new(Some, "misprinted", null);

    private static readonly Dictionary<string, (Func<ISender, Task> Send, string[] Properties, string? Message)> Cases = new()
    {
        ["PostRefundCheck.TenantId"] = Case(Check with { TenantId = Guid.Empty }, "TenantId"),
        ["PostRefundCheck.CheckId"] = Case(Check with { CheckId = Guid.Empty }, "CheckId"),
        ["PostRefundCheck.CheckNumber.Zero"] = Case(Check with { CheckNumber = 0 }, "CheckNumber"),
        ["PostRefundCheck.CheckNumber.NineDigits"] = Case(Check with { CheckNumber = 100_000_000 }, "CheckNumber"),
        ["PostRefundCheck.Source"] = Case(Check with { Source = "refund" }, "Source",
            "Source must be one of: deposit, prepayment."),
        ["PostRefundCheck.Bucket.BankAccountId"] = Case(
            Check with { Bucket = new RefundBucket(Guid.Empty, null, null) }, "Bucket.BankAccountId"),
        ["PostRefundCheck.Amount"] = Case(Check with { Amount = 0m }, "Amount", AmountMessage),

        ["VoidRefundCheck.EntryId"] = Case(Void with { EntryId = Guid.Empty }, "EntryId"),
        ["VoidRefundCheck.Reason"] = Case(Void with { Reason = "" }, "Reason"),
        ["VoidRefundCheck.Reason.Length"] = Case(Void with { Reason = new string('r', 501) }, "Reason"),

        ["GetRefundCheckStatuses.EntryIds.Count"] = Query(new GetRefundCheckStatuses(Ids(1001)), "EntryIds",
            "Request at most 1000 entries at a time."),
        ["GetRefundableBalances.TenantIds"] = Query(new GetRefundableBalances([]), "TenantIds"),
        ["GetRefundableBalances.TenantIds.Count"] = Query(new GetRefundableBalances(Ids(501)), "TenantIds",
            "Request at most 500 tenants at a time."),
    };

    public static TheoryData<string> Refusals() => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_malformed_request_is_refused_by_its_own_rule_before_the_handler_runs(string name)
    {
        var (send, properties, message) = Cases[name];
        var sender = await UnusableSenderAsync();

        var refused = await Should.ThrowAsync<ValidationException>(() => send(sender));

        refused.Errors.Select(e => e.PropertyName).ShouldBe(properties);
        if (message is not null)
        {
            refused.Errors.First().ErrorMessage.ShouldBe(message);
        }
    }

    [Fact]
    public async Task The_batch_limits_admit_a_full_batch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);

        (await QueryAsync(scope, new GetRefundCheckStatuses(Ids(1000)), ct)).ShouldBeEmpty();
        (await QueryAsync(scope, new GetRefundableBalances(Ids(500)), ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_batch_is_answered_without_reading_anything()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = await UnusableSenderAsync();

        (await sender.Query(new GetRefundCheckStatuses([]), ct)).ShouldBeEmpty();

        // The validator refuses an empty tenant list, so this is the handler's own short circuit.
        var db = await DisposedContextAsync();
        (await new GetRefundableBalancesHandler(db).Handle(new GetRefundableBalances([]), ct)).ShouldBeEmpty();
    }

    // ---- what a check posts, and the bucket it draws on ----

    [Fact]
    public async Task A_deposit_check_draws_on_the_deposit_bucket_keeps_its_owner_and_names_its_number()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await SendAsync(scope, new CollectDeposit(_tenant, 1450m, Feb1, scope.DepositBankId, null, "dep:1"), ct);
        // Prepaid credit beside it, which a deposit check must not draw on.
        await SendAsync(scope, new CollectPrepayment(_tenant, 75m, Feb1, scope.TrustBankId, null, "pre:1"), ct);
        var checkId = UuidV7.NewId();

        var posted = await SendAsync(scope,
            new PostRefundCheck(_tenant, "Deposit", 1450m, Mar2, checkId, 1043, InternalNote: "handed over at move-out"), ct);

        posted.ShouldBe(new RefundCheckPosted(posted.EntryId, scope.DepositBankId, _property, _owner));
        var entry = await EntryAsync(scope, posted.EntryId, ct);
        (entry.EventType, entry.EventSubtype, entry.Description, entry.SourceRef, entry.InternalNote, entry.EntryDate)
            .ShouldBe(("RefundIssued", "deposits", "Refund check #1043 — security deposit", $"refund-check:{checkId}",
                "handed over at move-out", Mar2));
        (await ReadLinesAsync(scope, posted.EntryId, ct)).ShouldBe(
        [
            new(AccountCodes.SecurityDepositsHeld, 1450m, null, EntryBasis.Both, _tenant, _owner, _property, null, scope.DepositBankId),
            new(AccountCodes.TrustBank(scope.DepositBankId), null, 1450m, EntryBasis.Both, null, null, null, null, scope.DepositBankId),
        ], ignoreOrder: true);

        // The emptied deposit bucket is no longer offered; the prepaid credit still is.
        (await QueryAsync(scope, new GetRefundableBalances([_tenant]), ct)).ShouldBe(
            [new RefundableBalance(_tenant, "prepayment", scope.TrustBankId, null, null, 75m)]);
    }

    [Fact]
    public async Task A_prepayment_check_draws_on_the_bank_holding_the_credit_owner_null()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await SendAsync(scope, new CollectDeposit(_tenant, 1450m, Feb1, scope.DepositBankId, null, "dep:1"), ct);
        await SendAsync(scope, new CollectPrepayment(_tenant, 75m, Feb1, scope.TrustBankId, null, "pre:1"), ct);

        var posted = await SendAsync(scope, new PostRefundCheck(_tenant, "prepayment", 60m, Mar2, UuidV7.NewId(), 2001), ct);

        posted.ShouldBe(new RefundCheckPosted(posted.EntryId, scope.TrustBankId, null, null));
        var entry = await EntryAsync(scope, posted.EntryId, ct);
        (entry.EventSubtype, entry.Description).ShouldBe(("prepayments", "Refund check #2001 — prepaid credit"));
        (await ReadLinesAsync(scope, posted.EntryId, ct)).ShouldBe(
        [
            new(AccountCodes.TenantPrepayments, 60m, null, EntryBasis.Both, _tenant, null, null, null, scope.TrustBankId),
            new(AccountCodes.TrustBank(scope.TrustBankId), null, 60m, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task Credit_in_two_buckets_must_be_named_and_a_named_bucket_must_hold_the_money()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);

        var none = await Should.ThrowAsync<InsufficientLiabilityException>(
            () => SendAsync(scope, new PostRefundCheck(_tenant, "prepayment", 10m, Mar2, UuidV7.NewId(), 2001), ct));
        (none.Kind, none.Requested, none.Held, none.TenantId).ShouldBe((LiabilityKind.Refund, 10m, 0m, (Guid?)_tenant));

        await SendAsync(scope, new CollectPrepayment(_tenant, 75m, Feb1, scope.TrustBankId, null, "pre:trust"), ct);
        await SendAsync(scope, new CollectPrepayment(_tenant, 40m, Feb1, scope.DepositBankId, null, "pre:deposit"), ct);
        await SendAsync(scope, new CollectDeposit(_tenant, 500m, Feb1, scope.DepositBankId, null, "dep:1"), ct);

        var ambiguous = await Should.ThrowAsync<RefundBucketAmbiguousException>(
            () => SendAsync(scope, new PostRefundCheck(_tenant, "prepayment", 40m, Mar2, UuidV7.NewId(), 2001), ct));
        ambiguous.TenantId.ShouldBe(_tenant);

        // A deposit bucket is the exact (bank, property, owner) it was collected under.
        foreach (var wrong in new[]
        {
            new RefundBucket(scope.TrustBankId, _property, _owner),
            new RefundBucket(scope.DepositBankId, UuidV7.NewId(), _owner),
            new RefundBucket(scope.DepositBankId, _property, UuidV7.NewId()),
        })
        {
            var empty = await Should.ThrowAsync<InsufficientLiabilityException>(() => SendAsync(scope,
                new PostRefundCheck(_tenant, "deposit", 100m, Mar2, UuidV7.NewId(), 1043, wrong), ct));
            (empty.Requested, empty.Held).ShouldBe((100m, 0m));
        }

        var named = await SendAsync(scope, new PostRefundCheck(_tenant, "prepayment", 40m, Mar2, UuidV7.NewId(), 2002,
            new RefundBucket(scope.DepositBankId, null, null)), ct);
        named.BankAccountId.ShouldBe(scope.DepositBankId);
        var deposit = await SendAsync(scope, new PostRefundCheck(_tenant, "deposit", 500m, Mar2, UuidV7.NewId(), 1044,
            new RefundBucket(scope.DepositBankId, _property, _owner)), ct);
        deposit.ShouldBe(new RefundCheckPosted(deposit.EntryId, scope.DepositBankId, _property, _owner));
    }

    [Fact]
    public async Task The_buckets_are_read_under_the_posting_lock_so_credit_arriving_in_a_second_bank_is_seen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await SendAsync(scope, new CollectPrepayment(_tenant, 100m, Feb1, scope.TrustBankId, null, "pre:trust"), ct);

        // Read before the first commits, the trust bank would look like the only bucket.
        var race = await Lanes.RaceAsync(fixture, scope,
            async lane =>
            {
                await lane.Lock.AcquireAsync(ct);
                return await lane.Sender(Leases()).Send(
                    new CollectPrepayment(_tenant, 200m, Feb1, scope.DepositBankId, null, "pre:deposit"), ct);
            },
            lane => lane.Sender(Leases()).Send(new PostRefundCheck(_tenant, "prepayment", 50m, Mar2, UuidV7.NewId(), 2001), ct),
            ct);

        race.SecondError.ShouldBeOfType<RefundBucketAmbiguousException>();
    }

    // ---- the refundable balances the form is offered ----

    [Fact]
    public async Task Each_positive_bucket_is_offered_and_a_prepayment_bucket_never_above_the_tenants_total()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        // The tenant: a deposit, and prepaid credit of 150 in the deposit bank of which a pre-#475
        // application drew 50 from the trust bank — buckets of +150 and −50, a total of 100.
        await SendAsync(scope, new CollectDeposit(_tenant, 1450m, Feb1, scope.DepositBankId, null, "dep:1"), ct);
        await SendAsync(scope, new CollectPrepayment(_tenant, 150m, Feb1, scope.DepositBankId, null, "pre:1"), ct);
        await SendAsync(scope, new AddCharge(_tenant, 50m, Feb1, "rent", null, "rent:feb"), ct);
        await PostHistoricalCrossBankApplicationAsync(scope, 50m, ct);
        // The other tenant: a deposit and nothing prepaid.
        await SendAsync(scope, new CollectDeposit(_other, 900m, Feb1, scope.DepositBankId, null, "dep:2"), ct);

        var balances = await QueryAsync(scope, new GetRefundableBalances([_tenant, _other, _tenant]), ct);

        balances.ShouldBe(
        [
            new RefundableBalance(_tenant, "deposit", scope.DepositBankId, _property, _owner, 1450m),
            new RefundableBalance(_tenant, "prepayment", scope.DepositBankId, null, null, 100m),
            new RefundableBalance(_other, "deposit", scope.DepositBankId, _property, _owner, 900m),
        ], ignoreOrder: true);

        // The check draws on the same capped figure: 150 is refused, 100 posts.
        await Should.ThrowAsync<InsufficientLiabilityException>(
            () => SendAsync(scope, new PostRefundCheck(_tenant, "prepayment", 150m, Mar2, UuidV7.NewId(), 2001), ct));
        await SendAsync(scope, new PostRefundCheck(_tenant, "prepayment", 100m, Mar2, UuidV7.NewId(), 2002), ct);
        (await QueryAsync(scope, new GetRefundableBalances([_tenant]), ct)).ShouldBe(
            [new RefundableBalance(_tenant, "deposit", scope.DepositBankId, _property, _owner, 1450m)]);
    }

    // ---- the status each check reports ----

    [Fact]
    public async Task Each_check_reports_its_bank_lines_status_or_voided_and_nothing_else_is_reported()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await SendAsync(scope, new CollectDeposit(_tenant, 1450m, Feb1, scope.DepositBankId, null, "dep:1"), ct);
        await SendAsync(scope, new CollectPrepayment(_tenant, 300m, Feb1, scope.TrustBankId, null, "pre:1"), ct);

        var outstanding = await IssueAsync(scope, "deposit", 100m, 1001, ct);
        var cleared = await IssueAsync(scope, "deposit", 200m, 1002, ct);
        var voided = await IssueAsync(scope, "deposit", 300m, 1003, ct);
        var reconciled = await IssueAsync(scope, "prepayment", 100m, 1004, ct);
        var unclearedAgain = await IssueAsync(scope, "deposit", 50m, 1005, ct);
        var notAsked = await IssueAsync(scope, "deposit", 25m, 1006, ct);

        // A refund that is not a check, and a check-like reference on an entry that is not a refund.
        Guid notACheck = default;
        await scope.RunAsync(async () => notACheck = await Events(scope).PostAsync(new RefundIssued(
            _tenant, new Money(10m), Mar2, scope.DepositBankId, RefundSource.Deposits, "Refund", "manual-refund:1",
            _property, _owner), ct), ct);
        var lookalike = (await SendAsync(scope,
            new CollectDeposit(_tenant, 10m, Feb1, scope.DepositBankId, null, $"refund-check:{UuidV7.NewId()}"), ct)).EntryId;

        await ClearAsync(scope, [await BankLineAsync(scope, cleared.EntryId, ct)], true, ct);
        var reversal = await SendAsync(scope, new VoidRefundCheck(voided.EntryId, "misprinted", Mar5), ct);
        var again = await BankLineAsync(scope, unclearedAgain.EntryId, ct);
        await ClearAsync(scope, [again], true, ct);
        await ClearAsync(scope, [again], false, ct);
        await ReconcileTrustBankAsync(scope, reconciled.EntryId, ct);

        var statuses = await QueryAsync(scope, new GetRefundCheckStatuses(
            [outstanding.EntryId, cleared.EntryId, voided.EntryId, reconciled.EntryId, unclearedAgain.EntryId,
                notACheck, lookalike, outstanding.EntryId]), ct);

        statuses.ShouldBe(new Dictionary<Guid, RefundCheckStatus>
        {
            [outstanding.EntryId] = new("outstanding", null),
            [cleared.EntryId] = new("cleared", null),
            [voided.EntryId] = new("voided", reversal.EntryId),
            [reconciled.EntryId] = new("reconciled", null),
            [unclearedAgain.EntryId] = new("outstanding", null),
        }, ignoreOrder: true);
        statuses.ShouldNotContainKey(notAsked.EntryId);
    }

    // ---- the guarded void ----

    [Fact]
    public async Task A_void_reverses_the_check_on_the_date_given_restores_the_bucket_and_clears_both_bank_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await SendAsync(scope, new CollectDeposit(_tenant, 1450m, Feb1, scope.DepositBankId, null, "dep:1"), ct);
        var check = await IssueAsync(scope, "deposit", 1450m, 1043, ct);
        var clock = new StoppedClock(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero));

        var reversal = await SendAsync(scope, new VoidRefundCheck(check.EntryId, "misprinted", Mar5), ct, clock);

        var entry = await EntryAsync(scope, reversal.EntryId, ct);
        (entry.ReversesEntryId, entry.EntryDate, entry.InternalNote, entry.SourceRef)
            .ShouldBe(((Guid?)check.EntryId, Mar5, "misprinted", $"refund-check-void:{check.EntryId}"));
        (await ReadLinesAsync(scope, reversal.EntryId, ct)).ShouldBe(
        [
            new(AccountCodes.SecurityDepositsHeld, null, 1450m, EntryBasis.Both, _tenant, _owner, _property, null, scope.DepositBankId),
            new(AccountCodes.TrustBank(scope.DepositBankId), 1450m, null, EntryBasis.Both, null, null, null, null, scope.DepositBankId),
        ], ignoreOrder: true);

        (await StatusAsync(scope, await BankLineAsync(scope, check.EntryId, ct), ct)).ShouldBe(BankLineStatus.Cleared);
        (await StatusAsync(scope, await BankLineAsync(scope, reversal.EntryId, ct), ct)).ShouldBe(BankLineStatus.Cleared);
        (await QueryAsync(scope, new GetRefundableBalances([_tenant]), ct)).ShouldBe(
            [new RefundableBalance(_tenant, "deposit", scope.DepositBankId, _property, _owner, 1450m)]);
    }

    [Fact]
    public async Task A_void_with_no_date_lands_on_todays_date_by_the_clock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await SendAsync(scope, new CollectDeposit(_tenant, 1450m, Feb1, scope.DepositBankId, null, "dep:1"), ct);
        var check = await IssueAsync(scope, "deposit", 1450m, 1043, ct);
        var clock = new StoppedClock(new DateTimeOffset(2026, 3, 17, 23, 30, 0, TimeSpan.Zero));

        var reversal = await SendAsync(scope, new VoidRefundCheck(check.EntryId, "misprinted", null), ct, clock);

        (await EntryAsync(scope, reversal.EntryId, ct)).EntryDate.ShouldBe(new DateOnly(2026, 3, 17));
    }

    [Fact]
    public async Task A_second_void_is_already_reversed_and_a_cleared_check_cannot_be_voided()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await SendAsync(scope, new CollectDeposit(_tenant, 1450m, Feb1, scope.DepositBankId, null, "dep:1"), ct);
        var voided = await IssueAsync(scope, "deposit", 400m, 1043, ct);
        var paid = await IssueAsync(scope, "deposit", 500m, 1044, ct);
        await SendAsync(scope, new VoidRefundCheck(voided.EntryId, "misprinted", Mar5), ct);
        await ClearAsync(scope, [await BankLineAsync(scope, paid.EntryId, ct)], true, ct);

        var twice = await Should.ThrowAsync<AlreadyReversedException>(
            () => SendAsync(scope, new VoidRefundCheck(voided.EntryId, "misprinted", Mar5), ct));
        (twice.EntryId, twice.Reason).ShouldBe((voided.EntryId, AlreadyReversedReason.AlreadyReversed));

        var cleared = await Should.ThrowAsync<RefundCheckClearedException>(
            () => SendAsync(scope, new VoidRefundCheck(paid.EntryId, "lost", Mar5), ct));
        cleared.EntryId.ShouldBe(paid.EntryId);

        (await ReversalsAsync(scope, ct)).ShouldBe([voided.EntryId]);
    }

    [Fact]
    public async Task Only_a_refund_check_can_be_voided_as_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        var deposit = (await SendAsync(scope, new CollectDeposit(_tenant, 1450m, Feb1, scope.DepositBankId, null, "dep:1"), ct)).EntryId;
        var lookalike = (await SendAsync(scope,
            new CollectDeposit(_tenant, 10m, Feb1, scope.DepositBankId, null, $"refund-check:{UuidV7.NewId()}"), ct)).EntryId;
        Guid manualRefund = default;
        Guid noReference = default;
        await scope.RunAsync(async () =>
        {
            manualRefund = await Events(scope).PostAsync(new RefundIssued(
                _tenant, new Money(10m), Mar2, scope.DepositBankId, RefundSource.Deposits, "Refund", "manual-refund:1",
                _property, _owner), ct);
            noReference = await Events(scope).PostAsync(
                new InterestEarned(new Money(5m), Mar2, scope.DepositBankId, "Interest"), ct);
        }, ct);

        foreach (var id in new[] { deposit, lookalike, manualRefund, noReference, UuidV7.NewId() })
        {
            var refused = await Should.ThrowAsync<EntryNotFoundException>(
                () => SendAsync(scope, new VoidRefundCheck(id, "wrong entry", Mar5), ct));
            refused.EntryId.ShouldBe(id);
        }

        (await ReversalsAsync(scope, ct)).ShouldBeEmpty();
        await scope.RunAsync(async () => (await scope.Db.Set<BankLineState>().CountAsync(ct)).ShouldBe(0), ct);
    }

    [Fact]
    public async Task A_void_is_refused_without_an_organization_context_before_it_claims_the_bank_line()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await SendAsync(scope, new CollectDeposit(_tenant, 1450m, Feb1, scope.DepositBankId, null, "dep:1"), ct);
        var check = await IssueAsync(scope, "deposit", 1450m, 1043, ct);

        // The posting lock is given the scope's org; only the handler's own context is empty.
        var refused = await Should.ThrowAsync<InvalidOperationException>(() => scope.RunAsync(() =>
            new VoidRefundCheckHandler(scope.Db, new OrgContext(), new PostingLock(scope.Db, scope.Tenant), Reversal(scope),
                TimeProvider.System).Handle(new VoidRefundCheck(check.EntryId, "misprinted", Mar5), ct), ct));

        refused.Message.ShouldBe("VoidRefundCheck requires an ambient organization context.");
    }

    [Fact]
    public async Task A_void_takes_the_posting_lock_so_a_second_void_waits_and_is_answered_as_already_reversed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await SendAsync(scope, new CollectDeposit(_tenant, 1450m, Feb1, scope.DepositBankId, null, "dep:1"), ct);
        var check = await IssueAsync(scope, "deposit", 1450m, 1043, ct);

        // Without the lock the second would read no reversal yet and meet the first's claim on the bank
        // line instead, refusing the void as a check the bank has paid.
        var race = await Lanes.RaceAsync(fixture, scope,
            lane => lane.Sender(Leases()).Send(new VoidRefundCheck(check.EntryId, "misprinted", Mar5), ct),
            lane => lane.Sender(Leases()).Send(new VoidRefundCheck(check.EntryId, "misprinted", Mar5), ct), ct);

        race.SecondError.ShouldBeOfType<AlreadyReversedException>();
        (await ReversalsAsync(scope, ct)).ShouldBe([check.EntryId]);
    }

    // ---- helpers ----

    private Task<OrgScope> NewScope(CancellationToken ct) => ProvisionedScopeAsync(
        fixture, ct, owners: [_owner], tenants: [_tenant, _other], properties: [_property], units: [_unit]);

    private Leases Leases() => new(new Dictionary<Guid, TenantPostingDimensions>
    {
        [_tenant] = new(_owner, _property, _unit),
        [_other] = new(_owner, _property, _unit),
    });

    private static Guid[] Ids(int count) => [.. Enumerable.Range(0, count).Select(_ => UuidV7.NewId())];

    private static (Func<ISender, Task>, string[], string?) Case<T>(ICommand<T> command, string property, string? message = null) =>
        (sender => sender.Send(command), [property], message);

    private static (Func<ISender, Task>, string[], string?) Query<T>(IQuery<T> query, string property, string? message = null) =>
        (sender => sender.Query(query), [property], message);

    /// <summary>A context already disposed: a request that got past its validator would fail on its first read.</summary>
    private async Task<AppDbContext> DisposedContextAsync()
    {
        var db = fixture.CreateContext(fixture.AppConnectionString, new OrgContext(), new ActorContext());
        await db.DisposeAsync();
        return db;
    }

    private async Task<ISender> UnusableSenderAsync() =>
        AccountingSender.Create(await DisposedContextAsync(), new OrgContext(), new ActorContext(), Leases());

    private Task<RefundCheckPosted> IssueAsync(OrgScope scope, string source, decimal amount, int number, CancellationToken ct) =>
        SendAsync(scope, new PostRefundCheck(_tenant, source, amount, Mar2, UuidV7.NewId(), number), ct);

    /// <summary>Clears the check's and the prepayment's lines in the trust bank, then reconciles March at their net.</summary>
    private async Task ReconcileTrustBankAsync(OrgScope scope, Guid checkEntryId, CancellationToken ct)
    {
        List<Guid> lines = [];
        await scope.RunAsync(async () => lines = await scope.Db.Set<JournalLine>().AsNoTracking()
            .Where(l => l.BankAccountId == scope.TrustBankId && l.AccountClass == AccountClass.TrustBank)
            .Select(l => l.Id).ToListAsync(ct), ct);
        lines.Count.ShouldBe(2);
        lines.ShouldContain(await BankLineAsync(scope, checkEntryId, ct));
        await ClearAsync(scope, [.. lines], true, ct);
        var view = await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 3, 200m), ct);
        view.Difference.ShouldBe(0m);
        await SendAsync(scope, new FinalizeReconciliation(view.Id), ct);
    }

    /// <summary>The posting through the posting seam of a pre-#475 application drawn from the trust bank.</summary>
    private Task PostHistoricalCrossBankApplicationAsync(OrgScope scope, decimal amount, CancellationToken ct) =>
        scope.RunAsync(() => Posting(scope).PostAsync(new PostEntryRequest(Feb1, "PrepaymentApplied", null,
            "Historical cross-bank application", "historical:1",
            [
                new PostLineRequest(AccountCodes.TenantPrepayments, new Money(amount), null, EntryBasis.Both,
                    TenantId: _tenant, BankAccountId: scope.TrustBankId),
                new PostLineRequest(AccountCodes.TenantReceivable, null, new Money(amount), EntryBasis.Accrual,
                    PropertyId: _property, OwnerId: _owner, TenantId: _tenant),
                new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(amount), EntryBasis.Cash,
                    PropertyId: _property, OwnerId: _owner, BankAccountId: scope.TrustBankId),
            ]), ct), ct);

    private static Task ClearAsync(OrgScope scope, Guid[] ids, bool cleared, CancellationToken ct) =>
        scope.RunAsync(() => new ApplyClearancesHandler(scope.Db, scope.Tenant).Handle(new ApplyClearances(ids, cleared), ct), ct);

    private async Task<T> SendAsync<T>(OrgScope scope, ICommand<T> command, CancellationToken ct, TimeProvider? clock = null)
    {
        T result = default!;
        await scope.RunAsync(async () => result = await AccountingSender.Create(scope, Leases(), clock).Send(command, ct), ct);
        return result;
    }

    private async Task<T> QueryAsync<T>(OrgScope scope, IQuery<T> query, CancellationToken ct)
    {
        T result = default!;
        await scope.RunAsync(async () => result = await AccountingSender.Create(scope, Leases()).Query(query, ct), ct);
        return result;
    }

    private static async Task<JournalEntry> EntryAsync(OrgScope scope, Guid id, CancellationToken ct)
    {
        JournalEntry entry = null!;
        await scope.RunAsync(async () => entry = await scope.Db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == id, ct), ct);
        return entry;
    }

    private static async Task<Guid> BankLineAsync(OrgScope scope, Guid entryId, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await scope.Db.Set<JournalLine>().AsNoTracking()
            .Where(l => l.EntryId == entryId && l.AccountClass == AccountClass.TrustBank)
            .Select(l => l.Id).SingleAsync(ct), ct);
        return id;
    }

    private static async Task<BankLineStatus?> StatusAsync(OrgScope scope, Guid lineId, CancellationToken ct)
    {
        BankLineStatus? status = null;
        await scope.RunAsync(async () => status = await scope.Db.Set<BankLineState>().AsNoTracking()
            .Where(s => s.JournalLineId == lineId).Select(s => (BankLineStatus?)s.Status).SingleOrDefaultAsync(ct), ct);
        return status;
    }

    /// <summary>The entry each reversal in the org reverses.</summary>
    private static async Task<List<Guid>> ReversalsAsync(OrgScope scope, CancellationToken ct)
    {
        List<Guid> reversed = [];
        await scope.RunAsync(async () => reversed = await scope.Db.Set<JournalEntry>().AsNoTracking()
            .Where(e => e.ReversesEntryId != null).Select(e => e.ReversesEntryId!.Value).ToListAsync(ct), ct);
        return reversed;
    }
}
