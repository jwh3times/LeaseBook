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
/// Issue #498 evidence for the fee and batched-settlement model (ADR-053), run against the existing
/// engine. Nothing here is a payment processor or a new posting path: each test posts the lines the
/// specification names, through the templates and services that already exist, and shows what the
/// books then say. One example is deliberately negative — it shows the shortfall the specified guard
/// has to stop, which today nothing does.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PaymentSettlementEvidenceTests(PostgresFixture fixture)
{
    private readonly Guid _tenantA = UuidV7.NewId();
    private readonly Guid _tenantB = UuidV7.NewId();
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();
    private static readonly DateOnly ChargeDate = new(2026, 2, 1);
    private static readonly DateOnly BankDate = new(2026, 2, 6);

    [Fact]
    public async Task A_clean_batch_posts_one_receipt_per_payment_and_ties_to_the_bank_deposit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, _tenantA, 1000m, ct);
        await Charge(scope, _tenantB, 1000m, ct);

        // One payout of 1,400.00: two items, each netting exactly what its tenant paid toward the ledger.
        await Post(scope, Receipt(scope, _tenantA, 1000m, "payout:po_1:1"), ct);
        await Post(scope, Receipt(scope, _tenantB, 400m, "payout:po_1:2"), ct);

        await AssertBooks(scope, bank: 1400m, tenantA: 0m, tenantB: 600m, ownerEquity: 1400m, heldFees: 0m, ct);
        await AssertClean(scope, ct);
    }

    [Fact]
    public async Task A_fee_shortfall_is_borne_by_held_pm_fees_and_never_by_the_owner_or_the_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, _tenantA, 1000m, ct);
        await Charge(scope, _tenantB, 500m, ct);
        await Post(scope, Receipt(scope, _tenantA, 1000m, "payout:po_1:1"), ct);
        await Post(scope, new ManagementFeeAssessed(_owner, _property, new Money(80m), BankDate,
            scope.TrustBankId, "management fee"), ct);

        // Tenant B paid 500.00 toward the ledger. The processor kept 7.50 more than the fee quoted, so
        // the bank received 492.50. The tenant is credited the 500.00; the PM's held fees cover the 7.50.
        await Post(scope, Receipt(scope, _tenantB, 500m, "payout:po_2:1"), ct);
        var shortfall = await Post(scope, new BankFeeCharged(new Money(7.50m), BankDate, scope.TrustBankId,
            "Processor fee shortfall", "payout:po_2:1:fee"), ct);

        await AssertBooks(scope, bank: 1492.50m, tenantA: 0m, tenantB: 0m, ownerEquity: 1420m, heldFees: 72.50m, ct);
        (await ReadLinesAsync(scope, shortfall, ct)).ShouldAllBe(l => l.OwnerId == null && l.TenantId == null);
        await AssertClean(scope, ct);
    }

    [Fact]
    public async Task A_shortfall_beyond_held_fees_posts_today_and_leaves_the_bank_short_with_every_invariant_green()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, _tenantB, 500m, ct);
        await Post(scope, Receipt(scope, _tenantB, 500m, "payout:po_1:1"), ct);

        // No fees are held. The existing bank-fee template has no balance guard, so it posts.
        await Post(scope, new BankFeeCharged(new Money(7.50m), BankDate, scope.TrustBankId,
            "Processor fee shortfall", "payout:po_1:1:fee"), ct);

        // The owner is owed 500.00 and the bank holds 492.50. The trust equation still balances, because
        // "held PM fees" has gone to -7.50: the PM now owes the trust account, and nothing reports it.
        await AssertBooks(scope, bank: 492.50m, tenantA: 0m, tenantB: 0m, ownerEquity: 500m, heldFees: -7.50m, ct);
        await AssertClean(scope, ct);
        // This is why ADR-053 requires the guard: a shortfall posts only while held fees stay at or above zero.
    }

    [Fact]
    public async Task A_fee_surplus_becomes_held_pm_fees_and_reaches_no_owner_or_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, _tenantB, 250m, ct);
        await Post(scope, Receipt(scope, _tenantB, 250m, "payout:po_1:1"), ct);

        // The processor kept 0.40 less than the fee quoted, so the bank received 250.40.
        Guid surplus = default;
        await scope.RunAsync(async () => surplus = await Posting(scope).PostAsync(new PostEntryRequest(
            BankDate, "ProcessorFeeSurplus", null, "Processor fee surplus", "payout:po_1:1:fee",
            [
                new(AccountCodes.TrustBank(scope.TrustBankId), new Money(0.40m), null, EntryBasis.Both,
                    BankAccountId: scope.TrustBankId),
                new(AccountCodes.PmIncome, null, new Money(0.40m), EntryBasis.Both, BankAccountId: scope.TrustBankId),
            ]), ct), ct);

        await AssertBooks(scope, bank: 250.40m, tenantA: 0m, tenantB: 0m, ownerEquity: 250m, heldFees: 0.40m, ct);
        (await ReadLinesAsync(scope, surplus, ct)).ShouldAllBe(l => l.OwnerId == null && l.TenantId == null);
        await AssertClean(scope, ct);
    }

    [Fact]
    public async Task A_batch_with_a_return_posts_the_guarded_reversal_on_the_bank_date_and_ties_to_a_net_debit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, _tenantA, 1000m, ct);
        await Charge(scope, _tenantB, 1000m, ct);
        var first = await Post(scope, Receipt(scope, _tenantA, 600m, "payout:po_1:1"), ct);

        // A later payout: tenant B's 400.00 arrives and tenant A's 600.00 is returned. The bank is
        // debited the net 200.00.
        var returnDate = BankDate.AddDays(5);
        await scope.RunAsync(async () =>
        {
            await Events(scope).PostAsync(Receipt(scope, _tenantB, 400m, "payout:po_2:1") with { Date = returnDate }, ct);
            await ReturnHandler(scope).Handle(
                new ReturnTenantPayment(first, returnDate, "payout:po_2:2", "Returned in payout po_2"), ct);
        }, ct);

        await AssertBooks(scope, bank: 400m, tenantA: 1000m, tenantB: 600m, ownerEquity: 400m, heldFees: 0m, ct);
        await AssertClean(scope, ct);
    }

    [Fact]
    public async Task A_return_that_fails_its_guard_leaves_the_whole_batch_unposted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, _tenantA, 1000m, ct);
        await Charge(scope, _tenantB, 1000m, ct);
        var first = await Post(scope, Receipt(scope, _tenantA, 600m, "payout:po_1:1"), ct);
        await Post(scope, new OwnerDisbursed(_owner, new Money(600m), BankDate, scope.TrustBankId, "draw"), ct);
        var entries = 0;
        await scope.RunAsync(async () => entries = await scope.Db.Set<JournalEntry>().CountAsync(ct), ct);

        // The clean item is posted first and the return is refused. One unit of work: neither stays.
        var returnDate = BankDate.AddDays(5);
        await Should.ThrowAsync<PaymentReturnBlockedException>(() => scope.RunAsync(async () =>
        {
            await Events(scope).PostAsync(Receipt(scope, _tenantB, 400m, "payout:po_2:1") with { Date = returnDate }, ct);
            await ReturnHandler(scope).Handle(
                new ReturnTenantPayment(first, returnDate, "payout:po_2:2", "Returned in payout po_2"), ct);
        }, ct));

        await scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>().CountAsync(ct)).ShouldBe(entries), ct);
        await AssertBooks(scope, bank: 0m, tenantA: 400m, tenantB: 1000m, ownerEquity: 0m, heldFees: 0m, ct);
        await AssertClean(scope, ct);
    }

    // The fee rule as a processor publishes it: a percentage of the amount charged plus a fixed amount,
    // optionally capped. The rates are illustrative, not any provider's pricing.
    private static decimal Fee(decimal charged, decimal rate, decimal fixedFee, decimal? cap)
    {
        var fee = decimal.Round(charged * rate + fixedFee, 2, MidpointRounding.AwayFromZero);
        return cap is { } limit ? Math.Min(fee, limit) : fee;
    }

    // The specified gross-up: the smallest amount to charge whose net, after the fee, is the amount
    // the tenant pays toward the ledger.
    private static decimal GrossUp(decimal ledgerAmount, decimal rate, decimal fixedFee, decimal? cap)
    {
        var charged = ledgerAmount;
        while (charged - Fee(charged, rate, fixedFee, cap) < ledgerAmount) { charged += 0.01m; }
        return charged;
    }

    [Theory]
    [InlineData(0.029, 0.30, null)] // card-shaped
    [InlineData(0.008, 0.00, 5.00)] // bank-debit-shaped, capped
    public void Every_ledger_amount_has_a_charge_whose_net_is_exactly_that_amount(double rate, double fixedFee, double? cap)
    {
        var (r, f, c) = ((decimal)rate, (decimal)fixedFee, (decimal?)cap);
        // Each extra cent charged raises the net by one cent or by nothing, so no amount is skipped.
        for (var cents = 1; cents <= 300_000; cents += cents < 20_000 ? 1 : 37)
        {
            var ledgerAmount = cents / 100m;
            var charged = GrossUp(ledgerAmount, r, f, c);
            (charged - Fee(charged, r, f, c)).ShouldBe(ledgerAmount, $"ledger amount {ledgerAmount}");
        }
    }

    [Fact]
    public void The_worked_examples_in_the_specification_use_the_gross_up_rule()
    {
        (GrossUp(1000m, 0.029m, 0.30m, null), Fee(1030.17m, 0.029m, 0.30m, null)).ShouldBe((1030.17m, 30.17m));
        (GrossUp(500m, 0.029m, 0.30m, null), Fee(515.24m, 0.029m, 0.30m, null)).ShouldBe((515.24m, 15.24m));
        (GrossUp(250m, 0.029m, 0.30m, null), Fee(257.78m, 0.029m, 0.30m, null)).ShouldBe((257.78m, 7.78m));
        (GrossUp(1000m, 0.008m, 0m, 5m), Fee(1005m, 0.008m, 0m, 5m)).ShouldBe((1005m, 5m));
        (GrossUp(250m, 0.008m, 0m, 5m), Fee(252.02m, 0.008m, 0m, 5m)).ShouldBe((252.02m, 2.02m));
    }

    private Task<OrgScope> NewScope(CancellationToken ct) => ProvisionedScopeAsync(
        fixture, ct, owners: [_owner], tenants: [_tenantA, _tenantB], properties: [_property]);

    private Task<Guid> Charge(OrgScope scope, Guid tenant, decimal amount, CancellationToken ct) => Post(scope,
        new RentCharged(tenant, _property, _owner, null, new Money(amount), ChargeDate, "rent"), ct);

    private PaymentReceived Receipt(OrgScope scope, Guid tenant, decimal amount, string sourceRef) =>
        new(tenant, _property, _owner, new Money(amount), BankDate, PaymentMethod.Ach,
            scope.TrustBankId, "Tenant payment", sourceRef);

    private static ReturnTenantPaymentHandler ReturnHandler(OrgScope scope) =>
        new(scope.Db, new PostingLock(scope.Db, scope.Tenant), Reversal(scope));

    private static async Task<Guid> Post(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private async Task AssertBooks(OrgScope scope, decimal bank, decimal tenantA, decimal tenantB,
        decimal ownerEquity, decimal heldFees, CancellationToken ct) =>
        await scope.RunAsync(async () =>
        {
            var balances = new BalanceReader(scope.Db);
            (await new GetBankRegisterHandler(scope.Db).Handle(new GetBankRegister(scope.TrustBankId), ct))
                .Totals.Book.ShouldBe(bank, "bank book");
            (await new GetTenantLedgerHandler(scope.Db).Handle(new GetTenantLedger(_tenantA), ct))
                .Balance.ShouldBe(tenantA, "tenant A balance");
            (await new GetTenantLedgerHandler(scope.Db).Handle(new GetTenantLedger(_tenantB), ct))
                .Balance.ShouldBe(tenantB, "tenant B balance");
            (await balances.OwnerEquityCashAsync(_owner, ct)).ShouldBe(ownerEquity, "owner cash equity");
            (await balances.HeldFeesAsync(scope.TrustBankId, ct)).ShouldBe(heldFees, "held PM fees");
        }, ct);

    private static async Task AssertClean(OrgScope scope, CancellationToken ct) =>
        await scope.RunAsync(async () =>
        {
            var checks = new InvariantChecks(scope.Db);
            (await checks.CheckCoreAsync(ct)).ShouldBeEmpty();
            (await checks.CheckTrustEquationAsync(ct)).ShouldBeEmpty();
        }, ct);
}
