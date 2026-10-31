using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-05 worked examples: each event posts the exact §C.3 line set (account code, side, amount, basis,
/// dimensions incl. bank attribution), and every entry balances per basis.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PostingTemplatesTests(PostgresFixture fixture)
{
    // Fresh per test instance: the composite (org_id, dim_id) FK (ADR-013) forbids reusing a fixed id
    // across orgs (global PK), and these were shared across test classes — so each test mints its own.
    private readonly Guid Owner = UuidV7.NewId();
    private readonly Guid Property = UuidV7.NewId();
    private readonly Guid Unit = UuidV7.NewId();
    private readonly Guid Tenant = UuidV7.NewId();
    private readonly Guid Buyer = UuidV7.NewId();

    // Bank account codes embed the per-org bank id (ADR-013), so they are resolved from the scope.
    private static string TrustBank(OrgScope scope) => AccountCodes.TrustBank(scope.TrustBankId);
    private static string DepositBank(OrgScope scope) => AccountCodes.TrustBank(scope.DepositBankId);
    private static string PmBank(OrgScope scope) => AccountCodes.PmOperatingBank(scope.OperatingBankId);

    [Fact]
    public async Task RentCharged_accrues_receivable_and_owner_income()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        var id = await PostAsync(scope, new RentCharged(
            Tenant, Property, Owner, Unit, new Money(1450m), Feb(1), "Feb rent"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(2);
        var receivable = Debit(lines, AccountCodes.TenantReceivable);
        receivable.Debit.ShouldBe(1450m);
        receivable.Basis.ShouldBe(EntryBasis.Accrual);
        (receivable.TenantId, receivable.PropertyId, receivable.OwnerId, receivable.UnitId)
            .ShouldBe((Tenant, Property, Owner, Unit));
        var equity = Credit(lines, AccountCodes.OwnerEquity);
        equity.Credit.ShouldBe(1450m);
        equity.Basis.ShouldBe(EntryBasis.Accrual);
        AssertBalancesPerBasis(lines);
    }

    [Fact]
    public async Task PaymentReceived_exactly_clearing_the_receivable_has_no_prepayment_line()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(1450m), Feb(1), "rent"), ct);
        var id = await PostAsync(scope, new PaymentReceived(
            Tenant, Property, Owner, new Money(1450m), Feb(3), PaymentMethod.Ach, scope.TrustBankId, "Feb payment"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(3);
        Debit(lines, TrustBank(scope)).Debit.ShouldBe(1450m);
        Debit(lines, TrustBank(scope)).Basis.ShouldBe(EntryBasis.Both);
        Debit(lines, TrustBank(scope)).BankAccountId.ShouldBe(scope.TrustBankId);
        Credit(lines, AccountCodes.TenantReceivable).Credit.ShouldBe(1450m);
        Credit(lines, AccountCodes.TenantReceivable).Basis.ShouldBe(EntryBasis.Accrual);
        var equity = Credit(lines, AccountCodes.OwnerEquity);
        equity.Credit.ShouldBe(1450m);
        equity.Basis.ShouldBe(EntryBasis.Cash);
        equity.BankAccountId.ShouldBe(scope.TrustBankId);
        lines.ShouldNotContain(l => l.Code == AccountCodes.TenantPrepayments);
        AssertBalancesPerBasis(lines);
    }

    [Fact]
    public async Task PaymentReceived_overpaying_auto_splits_the_excess_to_prepayments()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(2150m), May(1), "rent"), ct);
        var id = await PostAsync(scope, new PaymentReceived(
            Tenant, Property, Owner, new Money(2225m), May(22), PaymentMethod.Ach, scope.TrustBankId, "overpay"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(4);
        Debit(lines, TrustBank(scope)).Debit.ShouldBe(2225m);
        Credit(lines, AccountCodes.TenantReceivable).Credit.ShouldBe(2150m); // up to the open receivable
        Credit(lines, AccountCodes.OwnerEquity).Credit.ShouldBe(2150m);
        var prepay = Credit(lines, AccountCodes.TenantPrepayments);
        prepay.Credit.ShouldBe(75m); // excess → prepayment liability, never a negative receivable
        prepay.Basis.ShouldBe(EntryBasis.Both);
        prepay.TenantId.ShouldBe(Tenant);
        AssertBalancesPerBasis(lines);
    }

    [Fact]
    public async Task DepositCollected_records_a_liability_and_never_income()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        var id = await PostAsync(scope, new DepositCollected(
            Tenant, Property, Owner, new Money(1450m), Feb(1), scope.DepositBankId, "move-in deposit"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(2);
        Debit(lines, DepositBank(scope)).Debit.ShouldBe(1450m);
        var held = Credit(lines, AccountCodes.SecurityDepositsHeld);
        held.Credit.ShouldBe(1450m);
        held.Basis.ShouldBe(EntryBasis.Both);
        held.BankAccountId.ShouldBe(scope.DepositBankId);

        // No income-class line in any basis, ever.
        lines.ShouldNotContain(l => l.Code == AccountCodes.PmIncome || l.Code == AccountCodes.OwnerEquity);
        AssertBalancesPerBasis(lines);
    }

    [Fact]
    public async Task DepositApplied_to_owner_income_moves_funds_and_recognizes_equity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        await PostAsync(scope, new DepositCollected(Tenant, Property, Owner, new Money(1450m), Feb(1), scope.DepositBankId, "deposit"), ct);
        var id = await PostAsync(scope, new DepositApplied(
            Tenant, Property, Owner, new Money(1450m), May(31), scope.DepositBankId, scope.TrustBankId,
            DepositApplication.ToOwnerIncome, "damages"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(4);
        Debit(lines, AccountCodes.SecurityDepositsHeld).Debit.ShouldBe(1450m); // liability ↓
        Credit(lines, DepositBank(scope)).Credit.ShouldBe(1450m);              // deposit bank ↓
        Debit(lines, TrustBank(scope)).Debit.ShouldBe(1450m);                  // operating bank ↑
        var equity = Credit(lines, AccountCodes.OwnerEquity);
        equity.Credit.ShouldBe(1450m);
        equity.Basis.ShouldBe(EntryBasis.Both); // income recognized identically in both bases
        AssertBalancesPerBasis(lines);
    }

    [Fact]
    public async Task DepositApplied_against_charges_splits_into_receivable_and_cash_income()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        await PostAsync(scope, new DepositCollected(Tenant, Property, Owner, new Money(1450m), Feb(1), scope.DepositBankId, "deposit"), ct);
        // A receivable must exist before applying a deposit against charges (ADR-011 / P51).
        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(1450m), Feb(1), "rent"), ct);
        var id = await PostAsync(scope, new DepositApplied(
            Tenant, Property, Owner, new Money(1450m), May(31), scope.DepositBankId, scope.TrustBankId,
            DepositApplication.AgainstCharges, "applied to balance"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(5);
        var receivable = Credit(lines, AccountCodes.TenantReceivable);
        receivable.Credit.ShouldBe(1450m);
        receivable.Basis.ShouldBe(EntryBasis.Accrual);
        var equity = Credit(lines, AccountCodes.OwnerEquity);
        equity.Credit.ShouldBe(1450m);
        equity.Basis.ShouldBe(EntryBasis.Cash);
        AssertBalancesPerBasis(lines);
    }

    [Fact]
    public async Task ManagementFeeAssessed_credits_pm_income_with_no_owner_dimension()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        var id = await PostAsync(scope, new ManagementFeeAssessed(
            Owner, Property, new Money(290m), May(27), scope.TrustBankId, "May management fee"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(2);
        Debit(lines, AccountCodes.OwnerEquity).Debit.ShouldBe(290m);
        var pmIncome = Credit(lines, AccountCodes.PmIncome);
        pmIncome.Credit.ShouldBe(290m);
        pmIncome.OwnerId.ShouldBeNull(); // structural PM/owner isolation (P25)
        pmIncome.BankAccountId.ShouldBe(scope.TrustBankId);
        AssertBalancesPerBasis(lines);
    }

    [Fact]
    public async Task PMFeesSwept_moves_cash_and_income_attribution_in_four_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        await PostAsync(scope, new ManagementFeeAssessed(Owner, Property, new Money(290m), May(27), scope.TrustBankId, "fee"), ct);
        var id = await PostAsync(scope, new PMFeesSwept(new Money(290m), May(27), scope.TrustBankId, scope.OperatingBankId, "sweep"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(4);
        Credit(lines, TrustBank(scope)).Credit.ShouldBe(290m);   // trust bank ↓
        Debit(lines, PmBank(scope)).Debit.ShouldBe(290m);        // PM operating ↑
        // pm_income moves from the trust-bank dim to the PM-bank dim; net income unchanged.
        var pmDebit = lines.Single(l => l.Code == AccountCodes.PmIncome && l.Debit is not null);
        pmDebit.BankAccountId.ShouldBe(scope.TrustBankId);
        var pmCredit = lines.Single(l => l.Code == AccountCodes.PmIncome && l.Credit is not null);
        pmCredit.BankAccountId.ShouldBe(scope.OperatingBankId);
        lines.ShouldAllBe(l => l.OwnerId == null); // pm_income never carries an owner
        AssertBalancesPerBasis(lines);
    }

    [Fact]
    public async Task OwnerDisbursed_reduces_owner_equity_and_the_trust_bank()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        await PostAsync(scope, new OwnerContribution(Owner, Property, new Money(10000m), Feb(1), scope.TrustBankId, "seed"), ct);
        var id = await PostAsync(scope, new OwnerDisbursed(Owner, new Money(8200m), Jun(2), scope.TrustBankId, "owner draw"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(2);
        Debit(lines, AccountCodes.OwnerEquity).Debit.ShouldBe(8200m);
        Credit(lines, TrustBank(scope)).Credit.ShouldBe(8200m);
        AssertBalancesPerBasis(lines);
    }

    [Fact]
    public async Task CreditIssued_posts_only_accrual_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        var id = await PostAsync(scope, new CreditIssued(Tenant, Property, Owner, new Money(85m), Feb(17), "goodwill",
            AllowUnappliedCredit: true), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(2);
        Debit(lines, AccountCodes.OwnerEquity).Basis.ShouldBe(EntryBasis.Accrual);
        Credit(lines, AccountCodes.TenantReceivable).Basis.ShouldBe(EntryBasis.Accrual);
        lines.ShouldAllBe(l => l.Basis == EntryBasis.Accrual); // no cash lines
        AssertBalancesPerBasis(lines);
    }

    [Fact]
    public async Task BalanceForward_posts_an_arbitrary_balanced_set_all_both_basis()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        Guid id = default;
        await scope.RunAsync(async () =>
        {
            id = await Events(scope).PostAsync(new BalanceForwardRequest(
                Feb(1),
                [
                    new BalanceForwardLine(TrustBank(scope), new Money(5000m), null, BankAccountId: scope.TrustBankId),
                    new BalanceForwardLine(AccountCodes.OwnerEquity, null, new Money(5000m), OwnerId: Owner, BankAccountId: scope.TrustBankId),
                ],
                "cutover"), ct);
        }, ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(2);
        lines.ShouldAllBe(l => l.Basis == EntryBasis.Both);
        AssertBalancesPerBasis(lines);
    }

    [Fact]
    public async Task OwnerDisbursed_below_the_reserve_floor_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        await PostAsync(scope, new OwnerContribution(Owner, Property, new Money(1000m), Feb(1), scope.TrustBankId, "seed"), ct);

        var ex = await Should.ThrowAsync<ReserveFloorException>(() => scope.RunAsync(() =>
            Events(scope).PostAsync(new OwnerDisbursed(
                Owner, new Money(900m), Feb(15), scope.TrustBankId, "draw", Reserve: new Money(500m)), ct), ct));
        ex.Code.ShouldBe("reserve_floor"); // 1000 - 900 = 100 < 500
    }

    [Fact]
    public async Task DepositApplied_over_the_held_amount_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        await PostAsync(scope, new DepositCollected(Tenant, Property, Owner, new Money(1000m), Feb(1), scope.DepositBankId, "deposit"), ct);

        var ex = await Should.ThrowAsync<InsufficientLiabilityException>(() => scope.RunAsync(() =>
            Events(scope).PostAsync(new DepositApplied(
                Tenant, Property, Owner, new Money(1500m), May(31), scope.DepositBankId, scope.TrustBankId,
                DepositApplication.ToOwnerIncome, "too much"), ct), ct));
        ex.Code.ShouldBe("insufficient_liability");
    }

    [Fact]
    public async Task Posting_an_event_into_a_closed_period_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        await scope.RunAsync(() => Periods(scope).CloseAsync(2026, 2, ct), ct);

        await Should.ThrowAsync<PeriodClosedException>(() => scope.RunAsync(() =>
            Events(scope).PostAsync(new RentCharged(
                Tenant, Property, Owner, Unit, new Money(1450m), Feb(1), "into closed period"), ct), ct));
    }

    [Fact]
    public async Task Each_template_stamps_its_entry_with_the_event_type_and_subtype_the_journal_keys_on()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner, Buyer], tenants: [Tenant], properties: [Property], units: [Unit]);

        // Stored in the append-only journal and read back by every ledger, statement and report by name.
        (AccountingEvent Event, string Type, string? Subtype)[] cases =
        [
            (new DepositCollected(Tenant, Property, Owner, new Money(1000m), Feb(1), scope.DepositBankId, "deposit"),
                "DepositCollected", null),
            (new PrepaymentReceived(Tenant, Property, Owner, new Money(300m), Feb(1), scope.TrustBankId, "prepay"),
                "PrepaymentReceived", null),
            (new DepositResponsibilityTransferred(Property, Owner, Buyer, Feb(2),
                [new DepositResponsibilityTransferPosition(Tenant, scope.DepositBankId, new Money(1000m))], "sale"),
                "DepositResponsibilityTransferred", null),
            (new InterestEarned(new Money(40m), Feb(3), scope.TrustBankId, "interest"), "InterestEarned", null),
            (new BankFeeCharged(new Money(5m), Feb(3), scope.TrustBankId, "service charge"), "BankFeeCharged", null),
            (new TrustTransfer(new Money(10m), Feb(4), scope.TrustBankId, scope.DepositBankId, "move held fees"),
                "TrustTransfer", null),
            (new PMFeesSwept(new Money(20m), Feb(5), scope.TrustBankId, scope.OperatingBankId, "sweep"), "PMFeesSwept", null),
            (new RefundIssued(Tenant, new Money(100m), Feb(6), scope.TrustBankId, RefundSource.Prepayments, "refund"),
                "RefundIssued", "prepayments"),
            (new RefundIssued(Tenant, new Money(100m), Feb(6), scope.DepositBankId, RefundSource.Deposits, "refund",
                PropertyId: Property, OwnerId: Buyer), "RefundIssued", "deposits"),
            (Fee(FeeKind.Late), "FeeCharged", "late"),
            (Fee(FeeKind.MaintenanceRecharge), "FeeCharged", "maintenance-recharge"),
            (Fee(FeeKind.Other), "FeeCharged", "other"),
            (Payment(scope, PaymentMethod.Ach), "PaymentReceived", "ACH"),
            (Payment(scope, PaymentMethod.Card), "PaymentReceived", "Card"),
            (Payment(scope, PaymentMethod.Check), "PaymentReceived", "Check"),
            (Payment(scope, PaymentMethod.Cash), "PaymentReceived", "Cash"),
        ];

        foreach (var (businessEvent, type, subtype) in cases)
        {
            var entry = await ReadEntryAsync(scope, await PostAsync(scope, businessEvent, ct), ct);
            (entry.EventType, entry.EventSubtype).ShouldBe((type, subtype), businessEvent.GetType().Name);
        }
    }

    [Fact]
    public async Task BalanceForward_stamps_its_entry_as_a_balance_forward()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner]);

        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(new BalanceForwardRequest(
            Feb(1),
            [
                new BalanceForwardLine(TrustBank(scope), new Money(5000m), null, BankAccountId: scope.TrustBankId),
                new BalanceForwardLine(
                    AccountCodes.OwnerEquity, null, new Money(5000m), OwnerId: Owner, BankAccountId: scope.TrustBankId),
            ],
            "cutover", "bf:2026-02"), ct), ct);

        var entry = await ReadEntryAsync(scope, id, ct);
        (entry.EventType, entry.EventSubtype, entry.Description, entry.SourceRef)
            .ShouldBe(("BalanceForward", (string?)null, "cutover", "bf:2026-02"));
    }

    [Fact]
    public async Task VendorPaid_draws_owner_equity_and_the_bank_and_names_the_payee()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], properties: [Property]);
        await PostAsync(scope, new OwnerContribution(Owner, Property, new Money(1000m), Feb(1), scope.TrustBankId, "seed"), ct);

        var id = await PostAsync(scope, new VendorPaid(
            Owner, Property, new Money(240m), Feb(10), scope.TrustBankId, "Acme Plumbing", "kitchen leak", "bill:77"), ct);

        (await ReadLinesAsync(scope, id, ct)).ShouldBe(
        [
            new(AccountCodes.OwnerEquity, 240m, null, EntryBasis.Both, null, Owner, Property, null, scope.TrustBankId),
            new(TrustBank(scope), null, 240m, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
        ], ignoreOrder: true);
        var entry = await ReadEntryAsync(scope, id, ct);
        (entry.EventType, entry.Description, entry.SourceRef)
            .ShouldBe(("VendorPaid", "Vendor payment to Acme Plumbing — kitchen leak", "bill:77"));
    }

    [Fact]
    public async Task VendorPaid_below_the_reserve_floor_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], properties: [Property]);
        await PostAsync(scope, new OwnerContribution(Owner, Property, new Money(1000m), Feb(1), scope.TrustBankId, "seed"), ct);

        var ex = await Should.ThrowAsync<ReserveFloorException>(() => PostAsync(scope, new VendorPaid(
            Owner, Property, new Money(900m), Feb(10), scope.TrustBankId, "Acme Plumbing", "repair",
            Reserve: new Money(500m)), ct));

        // 1000 - 900 = 100 < 500.
        (ex.Amount, ex.Equity, ex.Reserve, ex.OwnerId).ShouldBe((900m, 1000m, 500m, Owner));
    }

    [Fact]
    public async Task A_deposit_refund_releases_the_collecting_bucket_and_a_prepayment_refund_stays_owner_null()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property]);
        await PostAsync(scope, new DepositCollected(Tenant, Property, Owner, new Money(1000m), Feb(1), scope.DepositBankId, "deposit"), ct);
        await PostAsync(scope, new PrepaymentReceived(Tenant, Property, Owner, new Money(300m), Feb(1), scope.TrustBankId, "prepay"), ct);

        var deposit = await PostAsync(scope, new RefundIssued(
            Tenant, new Money(1000m), Feb(20), scope.DepositBankId, RefundSource.Deposits, "deposit back",
            PropertyId: Property, OwnerId: Owner), ct);
        // Prepayments are collected with no owner, so a caller's property and owner are not carried onto the release.
        var prepayment = await PostAsync(scope, new RefundIssued(
            Tenant, new Money(300m), Feb(20), scope.TrustBankId, RefundSource.Prepayments, "prepayment back",
            PropertyId: Property, OwnerId: Owner), ct);

        (await ReadLinesAsync(scope, deposit, ct)).ShouldBe(
        [
            new(AccountCodes.SecurityDepositsHeld, 1000m, null, EntryBasis.Both, Tenant, Owner, Property, null, scope.DepositBankId),
            new(DepositBank(scope), null, 1000m, EntryBasis.Both, null, null, null, null, scope.DepositBankId),
        ], ignoreOrder: true);
        (await ReadLinesAsync(scope, prepayment, ct)).ShouldBe(
        [
            new(AccountCodes.TenantPrepayments, 300m, null, EntryBasis.Both, Tenant, null, null, null, scope.TrustBankId),
            new(TrustBank(scope), null, 300m, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task A_refund_over_the_held_liability_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property]);
        await PostAsync(scope, new PrepaymentReceived(Tenant, Property, Owner, new Money(300m), Feb(1), scope.TrustBankId, "prepay"), ct);

        var ex = await Should.ThrowAsync<InsufficientLiabilityException>(() => PostAsync(scope, new RefundIssued(
            Tenant, new Money(500m), Feb(20), scope.TrustBankId, RefundSource.Prepayments, "too much"), ct));

        (ex.Kind, ex.Requested, ex.Held, ex.TenantId).ShouldBe((LiabilityKind.Refund, 500m, 300m, (Guid?)Tenant));
    }

    [Fact]
    public async Task A_prepayment_refund_is_bounded_by_the_tenants_total_as_well_as_its_bank()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property]);
        await scope.RunAsync(async () =>
        {
            await Events(scope).PostAsync(new PrepaymentReceived(
                Tenant, Property, Owner, new Money(400m), Feb(1), scope.DepositBankId, "prepay"), ct);

            // The pre-#475 application shape (see InvariantTests): it debited the bank its caller named, so the
            // deposit bank still shows 400 held and the trust bank -400, and the tenant holds nothing in all.
            await Posting(scope).PostAsync(new PostEntryRequest(
                Feb(10), "PrepaymentApplied", null, "Historical cross-bank application", "bad-apply",
                [
                    new PostLineRequest(AccountCodes.TenantPrepayments, new Money(400m), null, EntryBasis.Both,
                        TenantId: Tenant, BankAccountId: scope.TrustBankId),
                    new PostLineRequest(AccountCodes.TenantReceivable, null, new Money(400m), EntryBasis.Accrual,
                        PropertyId: Property, OwnerId: Owner, TenantId: Tenant),
                    new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(400m), EntryBasis.Cash,
                        PropertyId: Property, OwnerId: Owner, BankAccountId: scope.TrustBankId),
                ]), ct);
        }, ct);

        // The deposit bank's bucket alone would allow it (#473).
        var ex = await Should.ThrowAsync<InsufficientLiabilityException>(() => PostAsync(scope, new RefundIssued(
            Tenant, new Money(400m), Feb(20), scope.DepositBankId, RefundSource.Prepayments, "refund"), ct));

        (ex.Kind, ex.Requested, ex.Held).ShouldBe((LiabilityKind.Refund, 400m, 0m));
    }

    [Fact]
    public async Task PMFeesSwept_over_the_held_fees_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], properties: [Property]);
        await PostAsync(scope, new ManagementFeeAssessed(Owner, Property, new Money(290m), May(27), scope.TrustBankId, "fee"), ct);

        var ex = await Should.ThrowAsync<InsufficientLiabilityException>(() => PostAsync(scope,
            new PMFeesSwept(new Money(300m), May(27), scope.TrustBankId, scope.OperatingBankId, "sweep"), ct));

        (ex.Kind, ex.Requested, ex.Held, ex.TenantId).ShouldBe((LiabilityKind.FeeSweep, 300m, 290m, (Guid?)null));
    }

    [Fact]
    public async Task Posting_without_an_event_or_request_is_refused_by_name()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var service = Events(scope);

        var noEvent = await Should.ThrowAsync<ArgumentNullException>(() => service.PostAsync((AccountingEvent)null!, ct));
        var noForward = await Should.ThrowAsync<ArgumentNullException>(() => service.PostAsync((BalanceForwardRequest)null!, ct));
        var noOpening = await Should.ThrowAsync<ArgumentNullException>(() => service.PostOpeningPositionAsync(null!, ct));

        (noEvent.ParamName, noForward.ParamName, noOpening.ParamName).ShouldBe(("businessEvent", "request", "req"));
    }

    [Fact]
    public async Task An_event_with_no_posting_template_is_refused_by_name()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        var ex = await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Events(scope).PostAsync(new UnheardOfEvent(), ct));

        (ex.ParamName, ex.ActualValue).ShouldBe(("businessEvent", (object?)nameof(UnheardOfEvent)));
        ex.Message.ShouldStartWith("No posting template for this event.");
    }

    [Fact]
    public void A_bulk_run_refuses_an_event_it_has_no_template_for()
    {
        var sweep = new PMFeesSwept(new Money(20m), Feb(5), UuidV7.NewId(), UuidV7.NewId(), "sweep");

        var ex = Should.Throw<ArgumentException>(() => RunEventEntryBuilder.Build(sweep));

        ex.ParamName.ShouldBe("businessEvent");
        ex.Message.ShouldBe("PMFeesSwept is not a supported bulk-run accounting event. (Parameter 'businessEvent')");
    }

    private sealed record UnheardOfEvent : AccountingEvent;

    private FeeCharged Fee(FeeKind kind) =>
        new(Tenant, Property, Owner, Unit, new Money(25m), Feb(7), kind, "fee");

    private PaymentReceived Payment(OrgScope scope, PaymentMethod method) =>
        new(Tenant, Property, Owner, new Money(10m), Feb(8), method, scope.TrustBankId, "payment");

    private static async Task<JournalEntry> ReadEntryAsync(OrgScope scope, Guid id, CancellationToken ct)
    {
        JournalEntry entry = null!;
        await scope.RunAsync(async () =>
            entry = await scope.Db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == id, ct), ct);
        return entry;
    }

    private static async Task<Guid> PostAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private static LineView Debit(IEnumerable<LineView> lines, string code) =>
        lines.Single(l => l.Code == code && l.Debit is not null);

    private static LineView Credit(IEnumerable<LineView> lines, string code) =>
        lines.Single(l => l.Code == code && l.Credit is not null);

    private static void AssertBalancesPerBasis(IReadOnlyList<LineView> lines)
    {
        foreach (var basis in new[] { EntryBasis.Cash, EntryBasis.Accrual })
        {
            var debits = lines.Where(l => l.Basis == basis || l.Basis == EntryBasis.Both).Sum(l => l.Debit ?? 0m);
            var credits = lines.Where(l => l.Basis == basis || l.Basis == EntryBasis.Both).Sum(l => l.Credit ?? 0m);
            debits.ShouldBe(credits, $"entry should balance in {basis} basis");
        }
    }

    private static DateOnly Feb(int day) => new(2026, 2, day);

    private static DateOnly May(int day) => new(2026, 5, day);

    private static DateOnly Jun(int day) => new(2026, 6, day);
}
