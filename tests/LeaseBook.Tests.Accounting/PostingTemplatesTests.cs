using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
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
    private static readonly Guid Owner = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid Property = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid Unit = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    private static string TrustBank => AccountCodes.TrustBank(TrustBankId);
    private static string DepositBank => AccountCodes.TrustBank(DepositBankId);
    private static string PmBank => AccountCodes.PmOperatingBank(OperatingBankId);

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
            Tenant, Property, Owner, new Money(1450m), Feb(3), PaymentMethod.Ach, TrustBankId, "Feb payment"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(3);
        Debit(lines, TrustBank).Debit.ShouldBe(1450m);
        Debit(lines, TrustBank).Basis.ShouldBe(EntryBasis.Both);
        Debit(lines, TrustBank).BankAccountId.ShouldBe(TrustBankId);
        Credit(lines, AccountCodes.TenantReceivable).Credit.ShouldBe(1450m);
        Credit(lines, AccountCodes.TenantReceivable).Basis.ShouldBe(EntryBasis.Accrual);
        var equity = Credit(lines, AccountCodes.OwnerEquity);
        equity.Credit.ShouldBe(1450m);
        equity.Basis.ShouldBe(EntryBasis.Cash);
        equity.BankAccountId.ShouldBe(TrustBankId);
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
            Tenant, Property, Owner, new Money(2225m), May(22), PaymentMethod.Ach, TrustBankId, "overpay"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(4);
        Debit(lines, TrustBank).Debit.ShouldBe(2225m);
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
            Tenant, Property, Owner, new Money(1450m), Feb(1), DepositBankId, "move-in deposit"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(2);
        Debit(lines, DepositBank).Debit.ShouldBe(1450m);
        var held = Credit(lines, AccountCodes.SecurityDepositsHeld);
        held.Credit.ShouldBe(1450m);
        held.Basis.ShouldBe(EntryBasis.Both);
        held.BankAccountId.ShouldBe(DepositBankId);

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

        await PostAsync(scope, new DepositCollected(Tenant, Property, Owner, new Money(1450m), Feb(1), DepositBankId, "deposit"), ct);
        var id = await PostAsync(scope, new DepositApplied(
            Tenant, Property, Owner, new Money(1450m), May(31), DepositBankId, TrustBankId,
            DepositApplication.ToOwnerIncome, "damages"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(4);
        Debit(lines, AccountCodes.SecurityDepositsHeld).Debit.ShouldBe(1450m); // liability ↓
        Credit(lines, DepositBank).Credit.ShouldBe(1450m);                     // deposit bank ↓
        Debit(lines, TrustBank).Debit.ShouldBe(1450m);                         // operating bank ↑
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

        await PostAsync(scope, new DepositCollected(Tenant, Property, Owner, new Money(1450m), Feb(1), DepositBankId, "deposit"), ct);
        // A receivable must exist before applying a deposit against charges (ADR-011 / P51).
        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(1450m), Feb(1), "rent"), ct);
        var id = await PostAsync(scope, new DepositApplied(
            Tenant, Property, Owner, new Money(1450m), May(31), DepositBankId, TrustBankId,
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
            Owner, Property, new Money(290m), May(27), TrustBankId, "May management fee"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(2);
        Debit(lines, AccountCodes.OwnerEquity).Debit.ShouldBe(290m);
        var pmIncome = Credit(lines, AccountCodes.PmIncome);
        pmIncome.Credit.ShouldBe(290m);
        pmIncome.OwnerId.ShouldBeNull(); // structural PM/owner isolation (P25)
        pmIncome.BankAccountId.ShouldBe(TrustBankId);
        AssertBalancesPerBasis(lines);
    }

    [Fact]
    public async Task PMFeesSwept_moves_cash_and_income_attribution_in_four_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        await PostAsync(scope, new ManagementFeeAssessed(Owner, Property, new Money(290m), May(27), TrustBankId, "fee"), ct);
        var id = await PostAsync(scope, new PMFeesSwept(new Money(290m), May(27), TrustBankId, OperatingBankId, "sweep"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(4);
        Credit(lines, TrustBank).Credit.ShouldBe(290m);   // trust bank ↓
        Debit(lines, PmBank).Debit.ShouldBe(290m);        // PM operating ↑
        // pm_income moves from the trust-bank dim to the PM-bank dim; net income unchanged.
        var pmDebit = lines.Single(l => l.Code == AccountCodes.PmIncome && l.Debit is not null);
        pmDebit.BankAccountId.ShouldBe(TrustBankId);
        var pmCredit = lines.Single(l => l.Code == AccountCodes.PmIncome && l.Credit is not null);
        pmCredit.BankAccountId.ShouldBe(OperatingBankId);
        lines.ShouldAllBe(l => l.OwnerId == null); // pm_income never carries an owner
        AssertBalancesPerBasis(lines);
    }

    [Fact]
    public async Task OwnerDisbursed_reduces_owner_equity_and_the_trust_bank()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        await PostAsync(scope, new OwnerContribution(Owner, Property, new Money(10000m), Feb(1), TrustBankId, "seed"), ct);
        var id = await PostAsync(scope, new OwnerDisbursed(Owner, new Money(8200m), Jun(2), TrustBankId, "owner draw"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(2);
        Debit(lines, AccountCodes.OwnerEquity).Debit.ShouldBe(8200m);
        Credit(lines, TrustBank).Credit.ShouldBe(8200m);
        AssertBalancesPerBasis(lines);
    }

    [Fact]
    public async Task CreditIssued_posts_only_accrual_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        var id = await PostAsync(scope, new CreditIssued(Tenant, Property, Owner, new Money(85m), Feb(17), "goodwill"), ct);
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
                    new BalanceForwardLine(TrustBank, new Money(5000m), null, BankAccountId: TrustBankId),
                    new BalanceForwardLine(AccountCodes.OwnerEquity, null, new Money(5000m), OwnerId: Owner, BankAccountId: TrustBankId),
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

        await PostAsync(scope, new OwnerContribution(Owner, Property, new Money(1000m), Feb(1), TrustBankId, "seed"), ct);

        var ex = await Should.ThrowAsync<ReserveFloorException>(() => scope.RunAsync(() =>
            Events(scope).PostAsync(new OwnerDisbursed(
                Owner, new Money(900m), Feb(15), TrustBankId, "draw", Reserve: new Money(500m)), ct), ct));
        ex.Code.ShouldBe("reserve_floor"); // 1000 - 900 = 100 < 500
    }

    [Fact]
    public async Task DepositApplied_over_the_held_amount_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

        await PostAsync(scope, new DepositCollected(Tenant, Property, Owner, new Money(1000m), Feb(1), DepositBankId, "deposit"), ct);

        var ex = await Should.ThrowAsync<InsufficientLiabilityException>(() => scope.RunAsync(() =>
            Events(scope).PostAsync(new DepositApplied(
                Tenant, Property, Owner, new Money(1500m), May(31), DepositBankId, TrustBankId,
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
