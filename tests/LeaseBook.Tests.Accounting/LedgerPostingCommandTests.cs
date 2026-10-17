using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// The ledger-posting commands the composer sends (M3, §C.1), dispatched through the real sender so
/// each command's validator runs in front of its handler as it does behind the endpoint. The handlers
/// resolve the tenant's dimensions and hand one business event to the engine; what is pinned here is
/// what they add on top: the rules that refuse a malformed command, the event each command chooses, the
/// description it falls back to, the bank a prepayment is applied from, and the refund check a plain
/// void refuses.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class LedgerPostingCommandTests(PostgresFixture fixture)
{
    private readonly Guid _tenant = UuidV7.NewId();
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();
    private readonly Guid _unit = UuidV7.NewId();
    private static readonly DateOnly Feb1 = new(2026, 2, 1);
    private static readonly DateOnly Feb5 = new(2026, 2, 5);

    // ---- validation: each rule refuses its own field, and nothing else does ----

    private static readonly Guid Some = UuidV7.NewId();
    private static readonly Guid Other = UuidV7.NewId();
    private const string AmountMessage = "Amount must be a positive value with at most 2 decimal places.";
    private const string ItemMessage = "A payout item is incomplete or of an unknown kind.";

    private static readonly AddCharge Charge = new(Some, 100m, Feb1, "rent", null, "src");
    private static readonly ApplyDeposit Apply = new(Some, 100m, Feb1, Some, Other, "to-owner-income", "damages", "src");
    private static readonly ApplyPrepayment Prepaid = new(Some, 100m, Feb1, null, null, "src");
    private static readonly CollectDeposit Deposit = new(Some, 100m, Feb1, Some, null, "src");
    private static readonly CollectPrepayment Prepay = new(Some, 100m, Feb1, Some, null, "src");
    private static readonly IssueCredit Credit = new(Some, 100m, Feb1, "goodwill", "src");
    private static readonly RecordPayment Payment = new(Some, 100m, Feb1, "ach", Some, null, "src");
    private static readonly ReturnTenantPayment Return = new(Some, Feb1, "src", "bank return");
    private static readonly VoidEntry Void = new(Some, "posted twice", null, "src");
    private static readonly TransferPropertyDeposits Transfer = new(Some, Some, Other, Feb1, "src");

    private static readonly PostPaymentSettlement Settlement = new(Some, Feb1, "po_1",
        [new SettlementReceipt("1", Some, 10m, "card", "Tenant payment"), new SettlementReturn("2", Some), new SettlementFee("3", 4m)]);

    private static PostPaymentSettlement WithLine(int index, SettlementLine line) =>
        Settlement with { Lines = [.. Settlement.Lines.Select((l, i) => i == index ? line : l)] };

    private static readonly Dictionary<string, (Func<ISender, Task> Send, string[] Properties, string? Message)> Cases = new()
    {
        ["AddCharge.TenantId"] = Case(Charge with { TenantId = Guid.Empty }, "TenantId"),
        ["AddCharge.SourceRef"] = Case(Charge with { SourceRef = "" }, "SourceRef"),
        ["AddCharge.Kind"] = Case(Charge with { Kind = "deposit" }, "Kind",
            "Kind must be one of: rent, late, maintenance-recharge, other."),
        ["AddCharge.Amount"] = Case(Charge with { Amount = 0m }, "Amount", AmountMessage),

        ["ApplyDeposit.TenantId"] = Case(Apply with { TenantId = Guid.Empty }, "TenantId"),
        ["ApplyDeposit.DepositBankId"] = Case(Apply with { DepositBankId = Guid.Empty }, "DepositBankId"),
        ["ApplyDeposit.OperatingBankId"] = Case(Apply with { OperatingBankId = Guid.Empty }, "OperatingBankId"),
        ["ApplyDeposit.SourceRef"] = Case(Apply with { SourceRef = "" }, "SourceRef"),
        ["ApplyDeposit.Reason"] = Case(Apply with { Reason = "" }, "Reason"),
        ["ApplyDeposit.Target"] = Case(Apply with { Target = "refund" }, "Target",
            "Target must be one of: to-owner-income, against-charges."),
        ["ApplyDeposit.Amount"] = Case(Apply with { Amount = -100m }, "Amount", AmountMessage),

        ["ApplyPrepayment.TenantId"] = Case(Prepaid with { TenantId = Guid.Empty }, "TenantId"),
        ["ApplyPrepayment.BankAccountId"] = Case(Prepaid with { BankAccountId = Guid.Empty }, "BankAccountId",
            "'Bank Account Id' must not be empty."),
        ["ApplyPrepayment.SourceRef"] = Case(Prepaid with { SourceRef = "" }, "SourceRef"),
        ["ApplyPrepayment.Amount"] = Case(Prepaid with { Amount = 100.005m }, "Amount", AmountMessage),

        ["CollectDeposit.TenantId"] = Case(Deposit with { TenantId = Guid.Empty }, "TenantId"),
        ["CollectDeposit.DepositBankId"] = Case(Deposit with { DepositBankId = Guid.Empty }, "DepositBankId"),
        ["CollectDeposit.SourceRef"] = Case(Deposit with { SourceRef = "" }, "SourceRef"),
        ["CollectDeposit.Amount"] = Case(Deposit with { Amount = 0m }, "Amount"),

        ["CollectPrepayment.TenantId"] = Case(Prepay with { TenantId = Guid.Empty }, "TenantId"),
        ["CollectPrepayment.BankAccountId"] = Case(Prepay with { BankAccountId = Guid.Empty }, "BankAccountId"),
        ["CollectPrepayment.SourceRef"] = Case(Prepay with { SourceRef = "" }, "SourceRef"),
        ["CollectPrepayment.Amount"] = Case(Prepay with { Amount = 0m }, "Amount"),

        ["IssueCredit.TenantId"] = Case(Credit with { TenantId = Guid.Empty }, "TenantId"),
        ["IssueCredit.SourceRef"] = Case(Credit with { SourceRef = "" }, "SourceRef"),
        ["IssueCredit.Reason"] = Case(Credit with { Reason = "" }, "Reason"),
        ["IssueCredit.Amount"] = Case(Credit with { Amount = 0m }, "Amount"),

        ["RecordPayment.TenantId"] = Case(Payment with { TenantId = Guid.Empty }, "TenantId"),
        ["RecordPayment.BankAccountId"] = Case(Payment with { BankAccountId = Guid.Empty }, "BankAccountId"),
        ["RecordPayment.SourceRef"] = Case(Payment with { SourceRef = "" }, "SourceRef"),
        ["RecordPayment.Method"] = Case(Payment with { Method = "wire" }, "Method",
            "Method must be one of: ach, card, check, cash."),
        ["RecordPayment.Amount"] = Case(Payment with { Amount = 0m }, "Amount"),

        ["ReturnTenantPayment.ReceiptEntryId"] = Case(Return with { ReceiptEntryId = Guid.Empty }, "ReceiptEntryId"),
        ["ReturnTenantPayment.ReturnDate"] = Case(Return with { ReturnDate = default }, "ReturnDate"),
        ["ReturnTenantPayment.SourceRef"] = Case(Return with { SourceRef = "" }, "SourceRef"),
        ["ReturnTenantPayment.InternalNote"] = Case(Return with { InternalNote = "" }, "InternalNote"),
        ["ReturnTenantPayment.InternalNote.Length"] = Case(Return with { InternalNote = new string('n', 501) }, "InternalNote"),

        ["VoidEntry.EntryId"] = Case(Void with { EntryId = Guid.Empty }, "EntryId"),
        ["VoidEntry.Reason"] = Case(Void with { Reason = "" }, "Reason"),
        ["VoidEntry.SourceRef"] = Case(Void with { SourceRef = "" }, "SourceRef"),

        ["TransferPropertyDeposits.PropertyId"] = Case(Transfer with { PropertyId = Guid.Empty }, "PropertyId"),
        ["TransferPropertyDeposits.FromOwnerId"] = Case(Transfer with { FromOwnerId = Guid.Empty }, "FromOwnerId"),
        ["TransferPropertyDeposits.ToOwnerId"] = Case(Transfer with { ToOwnerId = Guid.Empty }, "ToOwnerId"),
        ["TransferPropertyDeposits.ToOwnerId.Same"] = Case(Transfer with { ToOwnerId = Some }, "ToOwnerId"),
        ["TransferPropertyDeposits.EffectiveDate"] = Case(Transfer with { EffectiveDate = default }, "EffectiveDate"),
        ["TransferPropertyDeposits.SourceRef"] = Case(Transfer with { SourceRef = "" }, "SourceRef"),
        ["TransferPropertyDeposits.SourceRef.Length"] = Case(Transfer with { SourceRef = new string('s', 201) }, "SourceRef"),

        ["PostPaymentSettlement.BankAccountId"] = Case(Settlement with { BankAccountId = Guid.Empty }, "BankAccountId"),
        ["PostPaymentSettlement.BankDate"] = Case(Settlement with { BankDate = default }, "BankDate"),
        ["PostPaymentSettlement.PayoutReference.Length"] = Case(
            Settlement with { PayoutReference = new string('p', 101) }, "PayoutReference"),
        ["PostPaymentSettlement.Lines"] = Case(Settlement with { Lines = [] }, "Lines"),
        ["PostPaymentSettlement.Lines.Distinct"] = Case(
            WithLine(2, new SettlementFee("1", 4m)), "Lines", "Each item of a payout needs its own reference."),
        ["PostPaymentSettlement.Item"] = Case(WithLine(1, new SettlementReturn("", Some)), "Lines[1].Item"),
        ["PostPaymentSettlement.Item.Length"] = Case(WithLine(1, new SettlementReturn(new string('i', 61), Some)), "Lines[1].Item"),
        ["PostPaymentSettlement.FeeDifference"] = Case(WithLine(1, new SettlementReturn("2", Some, 0.005m)),
            "Lines[1].FeeDifference", "A fee difference must be an amount in whole cents."),
        ["PostPaymentSettlement.Receipt.TenantId"] = Case(
            WithLine(0, new SettlementReceipt("1", Guid.Empty, 10m, "card", "Tenant payment")), "Lines[0]", ItemMessage),
        ["PostPaymentSettlement.Receipt.Amount"] = Case(
            WithLine(0, new SettlementReceipt("1", Some, 0m, "card", "Tenant payment")), "Lines[0]", ItemMessage),
        ["PostPaymentSettlement.Receipt.Cents"] = Case(
            WithLine(0, new SettlementReceipt("1", Some, 10.005m, "card", "Tenant payment")), "Lines[0]", ItemMessage),
        ["PostPaymentSettlement.Receipt.Method"] = Case(
            WithLine(0, new SettlementReceipt("1", Some, 10m, "wire", "Tenant payment")), "Lines[0]", ItemMessage),
        ["PostPaymentSettlement.Return.ReceiptEntryId"] = Case(WithLine(1, new SettlementReturn("2", Guid.Empty)), "Lines[1]", ItemMessage),
        ["PostPaymentSettlement.Fee.Zero"] = Case(WithLine(2, new SettlementFee("3", 0m)), "Lines[2]", ItemMessage),
        ["PostPaymentSettlement.Fee.Negative"] = Case(WithLine(2, new SettlementFee("3", -4m)), "Lines[2]", ItemMessage),
        ["PostPaymentSettlement.UnknownKind"] = Case(WithLine(2, new UnknownLine("3")), "Lines[2]", ItemMessage),
    };

    public static TheoryData<string> Refusals() => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_malformed_command_is_refused_by_its_own_rule_before_the_handler_runs(string name)
    {
        var (send, properties, message) = Cases[name];
        // A context that never connects: a command that got past validation would fail on its first read.
        await using var db = fixture.CreateContext(fixture.AppConnectionString, new OrgContext(), new ActorContext());
        var sender = AccountingSender.Create(db, new OrgContext(), new ActorContext(), new Leases(new Dictionary<Guid, TenantPostingDimensions>()));

        var refused = await Should.ThrowAsync<ValidationException>(() => send(sender));

        refused.Errors.Select(e => e.PropertyName).ShouldBe(properties);
        if (message is not null)
        {
            refused.Errors.First().ErrorMessage.ShouldBe(message);
        }
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(12.345)]
    public void An_amount_that_is_not_a_positive_whole_number_of_cents_is_refused(decimal amount) =>
        new AddChargeValidator().Validate(Charge with { Amount = amount }).Errors
            .Select(e => (e.PropertyName, e.ErrorMessage)).ShouldBe([("Amount", AmountMessage)]);

    private static (Func<ISender, Task>, string[], string?) Case<T>(ICommand<T> command, string property, string? message = null) =>
        (sender => sender.Send(command), [property], message);

    // A kind of payout item the validator does not know.
    private sealed record UnknownLine(string Item) : SettlementLine(Item, 0m);

    // ---- what each command posts ----

    public static TheoryData<string, string, string?, string> ChargeKinds() => new()
    {
        { "rent", "RentCharged", null, "Rent" },
        { "late", "FeeCharged", "late", "Late fee" },
        { "maintenance-recharge", "FeeCharged", "maintenance-recharge", "Maintenance recharge" },
        { "other", "FeeCharged", "other", "Charge" },
    };

    [Theory]
    [MemberData(nameof(ChargeKinds))]
    public async Task A_charge_posts_the_event_its_kind_names_with_a_default_description(
        string kind, string eventType, string? subtype, string description)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);

        var id = await SendAsync(scope, new AddCharge(_tenant, 125m, Feb1, kind, null, $"charge:{kind}", "staff only"), ct);
        var named = await SendAsync(scope, new AddCharge(_tenant, 10m, Feb1, kind, "Owner-facing text", $"charge:{kind}:named"), ct);

        var entry = await EntryAsync(scope, id.EntryId, ct);
        (entry.EventType, entry.EventSubtype, entry.Description, entry.SourceRef, entry.InternalNote, entry.EntryDate)
            .ShouldBe((eventType, subtype, description, $"charge:{kind}", "staff only", Feb1));
        (await EntryAsync(scope, named.EntryId, ct)).Description.ShouldBe("Owner-facing text");
        (await ReadLinesAsync(scope, id.EntryId, ct)).ShouldBe(
        [
            new(AccountCodes.TenantReceivable, 125m, null, EntryBasis.Accrual, _tenant, _owner, _property, _unit, null),
            new(AccountCodes.OwnerEquity, null, 125m, EntryBasis.Accrual, null, _owner, _property, null, null),
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task Each_receipt_and_collection_falls_back_to_its_own_description_and_keeps_one_it_is_given()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await SendAsync(scope, new AddCharge(_tenant, 1000m, Feb1, "rent", null, "rent:feb"), ct);
        var bank = scope.TrustBankId;

        // Each command twice: once left to its default, once with the owner-facing text it is given.
        PostResult[] posted =
        [
            await SendAsync(scope, new RecordPayment(_tenant, 100m, Feb5, "check", bank, null, "pay:1"), ct),
            await SendAsync(scope, new RecordPayment(_tenant, 100m, Feb5, "check", bank, "Check 1042", "pay:2"), ct),
            await SendAsync(scope, new CollectDeposit(_tenant, 500m, Feb5, scope.DepositBankId, null, "dep:1"), ct),
            await SendAsync(scope, new CollectDeposit(_tenant, 500m, Feb5, scope.DepositBankId, "Pet deposit", "dep:2"), ct),
            await SendAsync(scope, new CollectPrepayment(_tenant, 300m, Feb5, bank, null, "pre:1"), ct),
            await SendAsync(scope, new CollectPrepayment(_tenant, 300m, Feb5, bank, "March rent early", "pre:2"), ct),
            await SendAsync(scope, new ApplyPrepayment(_tenant, 100m, Feb5, null, null, "app:1"), ct),
            await SendAsync(scope, new ApplyPrepayment(_tenant, 100m, Feb5, null, "Applied to February", "app:2"), ct),
        ];

        var entries = new List<(string, string?)>();
        foreach (var result in posted)
        {
            var entry = await EntryAsync(scope, result.EntryId, ct);
            entries.Add((entry.EventType, entry.Description));
        }

        entries.ShouldBe(
        [
            ("PaymentReceived", "Payment"), ("PaymentReceived", "Check 1042"),
            ("DepositCollected", "Security deposit"), ("DepositCollected", "Pet deposit"),
            ("PrepaymentReceived", "Prepayment"), ("PrepaymentReceived", "March rent early"),
            ("PrepaymentApplied", "Prepayment applied"), ("PrepaymentApplied", "Applied to February"),
        ]);
    }

    [Fact]
    public async Task A_tenant_with_no_lease_on_the_posting_date_is_a_validation_failure_on_the_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);

        var refused = await Should.ThrowAsync<ValidationException>(
            () => SendAsync(scope, new IssueCredit(UuidV7.NewId(), 50m, Feb1, "goodwill", "credit:1"), ct));

        refused.Errors.Select(e => (e.PropertyName, e.ErrorMessage)).ShouldBe(
        [
            ("tenantId", "This tenant has no lease effective on the posting date, so its owner/property cannot be resolved."),
        ]);
    }

    // ---- the bank a prepayment is applied from (#475) ----

    [Fact]
    public async Task A_named_bank_is_applied_from_even_when_prepaid_credit_sits_in_two_banks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await HoldInTwoBanksAsync(scope, ct);

        var applied = await SendAsync(scope, new ApplyPrepayment(_tenant, 150m, Feb5, scope.DepositBankId, null, "app:1"), ct);

        (await ReadLinesAsync(scope, applied.EntryId, ct)).ShouldBe(
        [
            new(AccountCodes.TenantPrepayments, 150m, null, EntryBasis.Both, _tenant, null, null, null, scope.DepositBankId),
            new(AccountCodes.TenantReceivable, null, 150m, EntryBasis.Accrual, _tenant, _owner, _property, null, null),
            new(AccountCodes.OwnerEquity, null, 150m, EntryBasis.Cash, null, _owner, _property, null, scope.DepositBankId),
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task With_no_bank_named_prepaid_credit_in_two_banks_is_ambiguous_and_in_none_is_insufficient()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await SendAsync(scope, new AddCharge(_tenant, 300m, Feb1, "rent", null, "rent:feb"), ct);

        var none = await Should.ThrowAsync<InsufficientLiabilityException>(
            () => SendAsync(scope, new ApplyPrepayment(_tenant, 100m, Feb5, null, null, "app:1"), ct));
        (none.Kind, none.Requested, none.Held, none.TenantId).ShouldBe((LiabilityKind.Prepayment, 100m, 0m, (Guid?)_tenant));

        await SendAsync(scope, new CollectPrepayment(_tenant, 100m, Feb1, scope.TrustBankId, null, "pre:trust"), ct);
        await SendAsync(scope, new CollectPrepayment(_tenant, 200m, Feb1, scope.DepositBankId, null, "pre:deposit"), ct);
        var two = await Should.ThrowAsync<PrepaymentBankAmbiguousException>(
            () => SendAsync(scope, new ApplyPrepayment(_tenant, 100m, Feb5, null, null, "app:2"), ct));
        two.TenantId.ShouldBe(_tenant);
    }

    [Fact]
    public async Task The_holding_bank_is_read_under_the_posting_lock_so_credit_arriving_in_a_second_bank_is_seen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await SendAsync(scope, new AddCharge(_tenant, 300m, Feb1, "rent", null, "rent:feb"), ct);
        await SendAsync(scope, new CollectPrepayment(_tenant, 100m, Feb1, scope.TrustBankId, null, "pre:trust"), ct);

        // The first unit of work holds the lock and leaves credit in the deposit bank as well; read
        // before that commits, the trust bank would look like the only one holding any.
        var race = await Lanes.RaceAsync(fixture, scope,
            async lane =>
            {
                await lane.Lock.AcquireAsync(ct);
                return await lane.Sender(Leases()).Send(
                    new CollectPrepayment(_tenant, 200m, Feb1, scope.DepositBankId, null, "pre:deposit"), ct);
            },
            lane => lane.Sender(Leases()).Send(new ApplyPrepayment(_tenant, 100m, Feb5, null, null, "app:1"), ct), ct);

        race.SecondError.ShouldBeOfType<PrepaymentBankAmbiguousException>();
    }

    // ---- a refund check is voided through its own command (#473) ----

    [Fact]
    public async Task A_refund_check_is_refused_by_a_plain_void_whatever_else_the_journal_holds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await SendAsync(scope, new CollectDeposit(_tenant, 500m, Feb1, scope.DepositBankId, null, "dep:1"), ct);
        Guid check = default;
        await scope.RunAsync(async () => check = await Events(scope).PostAsync(new RefundIssued(
            _tenant, new Money(200m), Feb5, scope.DepositBankId, RefundSource.Deposits, "Refund check #1001",
            "refund-check:1001", _property, _owner), ct), ct);

        var refused = await Should.ThrowAsync<RefundCheckVoidRequiredException>(
            () => SendAsync(scope, new VoidEntry(check, "printed in error", Feb5, "void:1"), ct));

        refused.EntryId.ShouldBe(check);
        await scope.RunAsync(async () =>
            (await scope.Db.Set<JournalEntry>().CountAsync(e => e.ReversesEntryId != null, ct)).ShouldBe(0), ct);
    }

    [Fact]
    public async Task A_void_with_no_date_lands_on_todays_date_by_the_clock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        var charge = await SendAsync(scope, new AddCharge(_tenant, 100m, Feb1, "rent", null, "rent:feb"), ct);
        var clock = new StoppedClock(new DateTimeOffset(2026, 2, 17, 23, 30, 0, TimeSpan.Zero));

        PostResult reversal = null!;
        await scope.RunAsync(async () => reversal = await AccountingSender.Create(scope, Leases(), clock)
            .Send(new VoidEntry(charge.EntryId, "posted twice", null, "void:1"), ct), ct);

        var entry = await EntryAsync(scope, reversal.EntryId, ct);
        (entry.ReversesEntryId, entry.EntryDate, entry.InternalNote, entry.SourceRef)
            .ShouldBe(((Guid?)charge.EntryId, new DateOnly(2026, 2, 17), "posted twice", "void:1"));
    }

    // ---- helpers ----

    private Task<OrgScope> NewScope(CancellationToken ct) => ProvisionedScopeAsync(
        fixture, ct, owners: [_owner], tenants: [_tenant], properties: [_property], units: [_unit]);

    private Leases Leases() => new(new Dictionary<Guid, TenantPostingDimensions>
    {
        [_tenant] = new(_owner, _property, _unit),
    });

    /// <summary>Prepaid credit of 100 in the trust bank and 200 in the deposit bank, against 300 owed.</summary>
    private async Task HoldInTwoBanksAsync(OrgScope scope, CancellationToken ct)
    {
        await SendAsync(scope, new AddCharge(_tenant, 300m, Feb1, "rent", null, "rent:feb"), ct);
        await SendAsync(scope, new CollectPrepayment(_tenant, 100m, Feb1, scope.TrustBankId, null, "pre:trust"), ct);
        await SendAsync(scope, new CollectPrepayment(_tenant, 200m, Feb1, scope.DepositBankId, null, "pre:deposit"), ct);
    }

    private async Task<T> SendAsync<T>(OrgScope scope, ICommand<T> command, CancellationToken ct)
    {
        T result = default!;
        await scope.RunAsync(async () => result = await AccountingSender.Create(scope, Leases()).Send(command, ct), ct);
        return result;
    }

    private static async Task<JournalEntry> EntryAsync(OrgScope scope, Guid id, CancellationToken ct)
    {
        JournalEntry entry = null!;
        await scope.RunAsync(async () => entry = await scope.Db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == id, ct), ct);
        return entry;
    }
}
