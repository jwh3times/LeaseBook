using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// Posting a processor payout as one batch (ADR-053, #500). The figures are the worked examples in
/// <c>docs/payments/fee-and-settlement-spec.md</c>, to the cent. <see cref="PaymentSettlementEvidenceTests"/>
/// showed the lines one at a time through the raw engine; these pin the command that posts them
/// together, in order, and undoes all of them when any rule refuses.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PostPaymentSettlementTests(PostgresFixture fixture)
{
    private readonly Guid _tenantA = UuidV7.NewId();
    private readonly Guid _tenantB = UuidV7.NewId();
    private readonly Guid _tenantC = UuidV7.NewId();
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();
    private static readonly DateOnly ChargeDate = new(2026, 2, 1);
    private static readonly DateOnly BankDate = new(2026, 2, 6);

    [Fact]
    public async Task The_worked_three_item_batch_posts_and_ties_to_the_deposit_to_the_cent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, _tenantA, 1000m, ct);
        await Charge(scope, _tenantB, 500m, ct);
        await Charge(scope, _tenantC, 250m, ct);
        await Post(scope, new InterestEarned(new Money(80m), ChargeDate, scope.TrustBankId, "fees held before the batch"), ct);

        // Bank evidence 1,742.90: 1,000.00 clean, 500.00 with a 7.50 shortfall, 250.00 with a 0.40 surplus.
        var result = await Settle(scope, "po_1", BankDate,
        [
            Receipt("1", _tenantA, 1000m, "card"),
            Receipt("2", _tenantB, 500m, "card", feeDifference: 7.50m),
            Receipt("3", _tenantC, 250m, "card", feeDifference: -0.40m),
        ], ct);

        result.Posted.ShouldBeTrue(result.Refusal);
        result.Postings.Select(p => (p.Item, p.Kind)).ShouldBe(
            [("1", "Receipt"), ("2", "Receipt"), ("3", "Receipt"), ("3", "FeeDifference"), ("2", "FeeDifference")]);
        await AssertBooks(scope, bank: 80m + 1742.90m, ownerEquity: 1750m, heldFees: 72.90m, ct);
        (await Balance(scope, _tenantA, ct), await Balance(scope, _tenantB, ct), await Balance(scope, _tenantC, ct))
            .ShouldBe((0m, 0m, 0m));
        await scope.RunAsync(async () =>
        {
            var entries = await scope.Db.Set<JournalEntry>().AsNoTracking()
                .Where(e => e.SourceRef != null && e.SourceRef.StartsWith("payout:po_1:")).ToListAsync(ct);
            entries.Select(e => e.SourceRef).ShouldBe(
                ["payout:po_1:1", "payout:po_1:2", "payout:po_1:3", "payout:po_1:2:fee", "payout:po_1:3:fee"], ignoreOrder: true);
            entries.ShouldAllBe(e => e.EntryDate == BankDate);
            entries.Where(e => e.EventType == "PaymentReceived").ShouldAllBe(e => e.EventSubtype == "Card");
            // The payout's own bank lines sum to the deposit, which is what lets it reconcile as one line.
            var ids = entries.Select(e => e.Id).ToArray();
            var bankLines = await scope.Db.Set<JournalLine>().AsNoTracking()
                .Where(l => ids.Contains(l.EntryId) && l.AccountClass == AccountClass.TrustBank).ToListAsync(ct);
            bankLines.Sum(l => (l.Debit?.Amount ?? 0m) - (l.Credit?.Amount ?? 0m)).ShouldBe(1742.90m);
        }, ct);
        await AssertClean(scope, ct);
    }

    [Fact]
    public async Task The_worked_batch_with_a_return_posts_both_on_the_bank_date_and_ties_to_a_net_debit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, _tenantA, 1000m, ct);
        await Charge(scope, _tenantB, 1000m, ct);
        var earlier = await Settle(scope, "po_1", BankDate, [Receipt("1", _tenantA, 600m, "ach")], ct);
        var receipt = earlier.Postings.Single().EntryId;

        var later = BankDate.AddDays(5);
        var result = await Settle(scope, "po_2", later,
            [new SettlementReturn("2", receipt), Receipt("1", _tenantB, 400m, "ach")], ct);

        result.Posted.ShouldBeTrue(result.Refusal);
        // The receipt posts before the return whatever order the processor lists them in.
        result.Postings.Select(p => (p.Item, p.Kind)).ShouldBe([("1", "Receipt"), ("2", "Return")]);
        await AssertBooks(scope, bank: 400m, ownerEquity: 400m, heldFees: 0m, ct);
        (await Balance(scope, _tenantA, ct), await Balance(scope, _tenantB, ct)).ShouldBe((1000m, 600m));
        await scope.RunAsync(async () =>
        {
            var reversal = await scope.Db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.ReversesEntryId == receipt, ct);
            (reversal.EntryDate, reversal.SourceRef).ShouldBe((later, "payout:po_2:2"));
        }, ct);
        await AssertClean(scope, ct);
    }

    [Fact]
    public async Task A_return_fee_with_no_payment_beside_it_is_a_shortfall_from_held_fees()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, _tenantA, 1000m, ct);
        await Post(scope, new InterestEarned(new Money(80m), ChargeDate, scope.TrustBankId, "held"), ct);
        var receipt = (await Settle(scope, "po_1", BankDate, [Receipt("1", _tenantA, 600m, "ach")], ct)).Postings.Single().EntryId;

        var result = await Settle(scope, "po_2", BankDate.AddDays(5),
            [new SettlementReturn("1", receipt), new SettlementFee("2", 4m)], ct);

        result.Posted.ShouldBeTrue(result.Refusal);
        await AssertBooks(scope, bank: 76m, ownerEquity: 0m, heldFees: 76m, ct);
        await AssertClean(scope, ct);
    }

    [Fact]
    public async Task A_surplus_in_the_same_payout_can_cover_a_shortfall_when_nothing_else_is_held()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, _tenantA, 1000m, ct);
        await Charge(scope, _tenantB, 500m, ct);

        var result = await Settle(scope, "po_1", BankDate,
            [Receipt("1", _tenantA, 1000m, "card", feeDifference: 0.30m), Receipt("2", _tenantB, 500m, "card", feeDifference: -0.40m)], ct);

        result.Posted.ShouldBeTrue(result.Refusal);
        await AssertBooks(scope, bank: 1500.10m, ownerEquity: 1500m, heldFees: 0.10m, ct);
        await AssertClean(scope, ct);
    }

    public static TheoryData<string, string, string> Refusals() => new()
    {
        { "shortfall", "pm_fees_insufficient", "2" },
        { "return", "return_owner_funds_disbursed", "3" },
        { "period", "period_closed", "1" },
        { "tenant", "attribution_unavailable", "4" },
        { "replay", "duplicate_source_ref", "1" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_refusal_names_the_rule_and_the_item_and_leaves_nothing_of_the_batch_posted(
        string scenario, string code, string item)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, _tenantA, 1000m, ct);
        await Charge(scope, _tenantB, 1000m, ct);
        await Charge(scope, _tenantC, 600m, ct);
        var earlier = (await Settle(scope, "po_0", BankDate, [Receipt("1", _tenantC, 600m, "ach")], ct)).Postings.Single().EntryId;
        var later = BankDate.AddDays(5);
        var lines = new List<SettlementLine> { Receipt("1", _tenantA, 400m, "ach") };
        var payout = "po_1";
        switch (scenario)
        {
            case "shortfall": // nothing is held, and nothing in the batch brings any
                lines.Add(Receipt("2", _tenantB, 500m, "card", feeDifference: 7.50m));
                break;
            case "return": // the owner has been paid out everything the earlier receipt brought, and more
                await Post(scope, new OwnerDisbursed(_owner, new Money(600m), BankDate, scope.TrustBankId, "draw"), ct);
                lines.Add(new SettlementReturn("3", earlier));
                lines[0] = Receipt("1", _tenantA, 100m, "ach"); // not enough to cover the 600.00 return
                break;
            case "period":
                await scope.RunAsync(() => Periods(scope).CloseAsync(later.Year, later.Month, ct), ct);
                later = new DateOnly(later.Year, later.Month, 20);
                break;
            case "tenant": // no lease on the bank date: the fake resolves no dimensions for this tenant
                lines.Add(Receipt("4", UuidV7.NewId(), 50m, "ach"));
                break;
            default:
                payout = "po_0"; // the earlier payout again
                later = BankDate;
                lines[0] = Receipt("1", _tenantC, 600m, "ach");
                break;
        }
        var before = await Snapshot(scope, ct);

        PaymentSettlementResult result = null!;
        await scope.RunAsync(async () =>
        {
            result = await Handler(scope).Handle(new PostPaymentSettlement(scope.TrustBankId, later, payout, lines), ct);
            // The caller's transaction is still usable, and saving again must not bring anything back.
            await scope.Db.SaveChangesAsync(ct);
        }, ct);

        (result.Posted, result.Refusal, result.RefusedItem).ShouldBe((false, code, item));
        result.Postings.ShouldBeEmpty();
        (await Snapshot(scope, ct)).ShouldBe(before);
        await AssertClean(scope, ct);
    }

    [Fact]
    public async Task A_payout_that_is_refused_can_be_posted_once_the_cause_is_gone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, _tenantA, 1000m, ct);
        SettlementLine[] lines = [Receipt("1", _tenantA, 500m, "card", feeDifference: 7.50m)];

        (await Settle(scope, "po_1", BankDate, lines, ct)).Refusal.ShouldBe("pm_fees_insufficient");
        await Post(scope, new InterestEarned(new Money(10m), ChargeDate, scope.TrustBankId, "now held"), ct);
        var result = await Settle(scope, "po_1", BankDate, lines, ct);

        result.Posted.ShouldBeTrue(result.Refusal);
        await AssertBooks(scope, bank: 502.50m, ownerEquity: 500m, heldFees: 2.50m, ct);
        await AssertClean(scope, ct);
    }

    private Task<OrgScope> NewScope(CancellationToken ct) => ProvisionedScopeAsync(
        fixture, ct, owners: [_owner], tenants: [_tenantA, _tenantB, _tenantC], properties: [_property]);

    private PostPaymentSettlementHandler Handler(OrgScope scope) => new(
        scope.Db, new PostingLock(scope.Db, scope.Tenant), new Dimensions(this), Events(scope), Reversal(scope));

    private static SettlementReceipt Receipt(string item, Guid tenant, decimal amount, string method, decimal feeDifference = 0m) =>
        new(item, tenant, amount, method, "Tenant payment", feeDifference);

    private async Task<PaymentSettlementResult> Settle(
        OrgScope scope, string payout, DateOnly bankDate, IReadOnlyList<SettlementLine> lines, CancellationToken ct)
    {
        PaymentSettlementResult result = null!;
        await scope.RunAsync(async () => result = await Handler(scope).Handle(
            new PostPaymentSettlement(scope.TrustBankId, bankDate, payout, lines), ct), ct);
        return result;
    }

    private Task<Guid> Charge(OrgScope scope, Guid tenant, decimal amount, CancellationToken ct) => Post(scope,
        new RentCharged(tenant, _property, _owner, null, new Money(amount), ChargeDate, "rent"), ct);

    private static async Task<Guid> Post(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private static async Task<decimal> Balance(OrgScope scope, Guid tenant, CancellationToken ct)
    {
        decimal balance = 0;
        await scope.RunAsync(async () => balance =
            (await new GetTenantLedgerHandler(scope.Db).Handle(new GetTenantLedger(tenant), ct)).Balance, ct);
        return balance;
    }

    // Entry count, bank book, held fees and owner equity: what a batch that posted nothing leaves unchanged.
    private async Task<(int, decimal, decimal, decimal)> Snapshot(OrgScope scope, CancellationToken ct)
    {
        (int, decimal, decimal, decimal) snapshot = default;
        await scope.RunAsync(async () =>
        {
            var balances = new BalanceReader(scope.Db);
            snapshot = (
                await scope.Db.Set<JournalEntry>().CountAsync(ct),
                (await new GetBankRegisterHandler(scope.Db).Handle(new GetBankRegister(scope.TrustBankId), ct)).Totals.Book,
                await balances.HeldFeesAsync(scope.TrustBankId, ct),
                await balances.OwnerEquityCashAsync(_owner, ct));
        }, ct);
        return snapshot;
    }

    private async Task AssertBooks(OrgScope scope, decimal bank, decimal ownerEquity, decimal heldFees, CancellationToken ct)
    {
        var (_, book, held, equity) = await Snapshot(scope, ct);
        (book, equity, held).ShouldBe((bank, ownerEquity, heldFees));
    }

    private static async Task AssertClean(OrgScope scope, CancellationToken ct) =>
        await scope.RunAsync(async () =>
        {
            var checks = new InvariantChecks(scope.Db);
            (await checks.CheckCoreAsync(ct)).ShouldBeEmpty();
            (await checks.CheckTrustEquationAsync(ct)).ShouldBeEmpty();
        }, ct);

    // The host's adapter reads Directory; here every provisioned tenant has one lease, and any other has none.
    private sealed class Dimensions(PostPaymentSettlementTests test) : ITenantPostingDimensions
    {
        public Task<TenantPostingDimensions?> GetAsync(Guid tenantId, DateOnly date, CancellationToken ct) =>
            Task.FromResult(tenantId == test._tenantA || tenantId == test._tenantB || tenantId == test._tenantC
                ? new TenantPostingDimensions(test._owner, test._property, null) : null);
    }

    // ---- a payout's bank lines are one group in the register and clear as one (ADR-053) ----

    [Fact]
    public async Task The_register_names_each_lines_payout_and_a_clearance_takes_the_whole_payout_or_none_of_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, _tenantA, 1000m, ct);
        await Charge(scope, _tenantB, 500m, ct);
        await Charge(scope, _tenantC, 250m, ct);
        await Post(scope, new InterestEarned(new Money(80m), ChargeDate, scope.TrustBankId, "fees held before the batch"), ct);
        (await Settle(scope, "po_1", BankDate,
        [
            Receipt("1", _tenantA, 1000m, "card"),
            Receipt("2", _tenantB, 500m, "card", feeDifference: 7.50m),
        ], ct)).Posted.ShouldBeTrue();
        // A second payout whose reference only starts the same way, and a separator inside an item.
        (await Settle(scope, "po_10", BankDate, [Receipt("a:b", _tenantC, 250m, "card", feeDifference: -0.40m)], ct))
            .Posted.ShouldBeTrue();

        // And one that po_1 would match if the underscore were read as a pattern's wildcard.
        await Charge(scope, _tenantC, 40m, ct);
        (await Settle(scope, "poX1", BankDate, [Receipt("1", _tenantC, 40m, "card")], ct)).Posted.ShouldBeTrue();

        var rows = await Register(scope, ct);
        rows.Count.ShouldBe(7);
        rows.Where(r => r.PayoutReference == "po_1").Sum(Signed).ShouldBe(1492.50m);
        rows.Where(r => r.PayoutReference == "po_10").Sum(Signed).ShouldBe(250.40m);
        rows.Single(r => r.PayoutReference is null).Deposit.ShouldBe(80m);
        await scope.RunAsync(async () =>
        {
            var references = await new GetPayoutReferencesHandler(scope.Db)
                .Handle(new GetPayoutReferences([.. rows.Select(r => r.JournalLineId)]), ct);
            references.ShouldBe(rows.Where(r => r.PayoutReference is not null)
                .ToDictionary(r => r.JournalLineId, r => r.PayoutReference!), ignoreOrder: true);
        }, ct);

        // Naming one line of po_1 clears all three of its lines and nothing else.
        var one = rows.First(r => r.PayoutReference == "po_1").JournalLineId;
        (await Clear(scope, [one], true, ct)).ShouldBe(3);
        rows = await Register(scope, ct);
        rows.Where(r => r.PayoutReference == "po_1").ShouldAllBe(r => r.Status == BankLineStatus.Cleared);
        rows.Where(r => r.PayoutReference != "po_1").ShouldAllBe(r => r.Status == BankLineStatus.Uncleared);

        // Unclearing a different line of it unclears all three.
        var other = rows.Last(r => r.PayoutReference == "po_1").JournalLineId;
        other.ShouldNotBe(one);
        (await Clear(scope, [other], false, ct)).ShouldBe(3);
        (await Register(scope, ct)).ShouldAllBe(r => r.Status == BankLineStatus.Uncleared);

        // A line no payout posted clears by itself, as it always has.
        (await Clear(scope, [rows.Single(r => r.PayoutReference is null).JournalLineId], true, ct)).ShouldBe(1);
        (await Register(scope, ct)).Count(r => r.Status == BankLineStatus.Cleared).ShouldBe(1);
    }

    [Theory]
    [InlineData("payout:po_1:1", "po_1")]
    [InlineData("payout:po_1:1:fee", "po_1")]
    [InlineData("payout:po_1:a:b", "po_1")]
    [InlineData("payout:po_1", null)]
    [InlineData("payout::1", null)]
    [InlineData("payment:po_1:1", null)]
    [InlineData(null, null)]
    public void A_payout_reference_reads_back_out_of_a_source_reference_exactly(string? sourceRef, string? reference) =>
        PayoutSourceRef.ReferenceOf(sourceRef).ShouldBe(reference);

    [Fact]
    public void A_payout_reference_with_the_separator_in_it_is_refused_before_anything_posts()
    {
        var command = new PostPaymentSettlement(UuidV7.NewId(), BankDate, "po:1", [Receipt("1", _tenantA, 10m, "card")]);

        new PostPaymentSettlementValidator().Validate(command).Errors
            .ShouldContain(e => e.ErrorMessage == "A payout reference cannot contain a colon.");
        new PostPaymentSettlementValidator().Validate(command with { PayoutReference = "po_1" }).IsValid.ShouldBeTrue();
    }

    private static decimal Signed(RegisterRow row) => (row.Deposit ?? 0m) - (row.Withdrawal ?? 0m);

    private static async Task<IReadOnlyList<RegisterRow>> Register(OrgScope scope, CancellationToken ct)
    {
        IReadOnlyList<RegisterRow> rows = [];
        await scope.RunAsync(async () => rows =
            (await new GetBankRegisterHandler(scope.Db).Handle(new GetBankRegister(scope.TrustBankId), ct)).Rows, ct);
        return rows;
    }

    private static async Task<int> Clear(OrgScope scope, Guid[] ids, bool cleared, CancellationToken ct)
    {
        var affected = 0;
        await scope.RunAsync(async () => affected =
            (await new ApplyClearancesHandler(scope.Db, scope.Tenant).Handle(new ApplyClearances(ids, cleared), ct)).Affected, ct);
        return affected;
    }
}
