using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// Issue #455 evidence against the existing engine, not an implementation of a payment processor.
/// Negative examples intentionally demonstrate why unsupported funds flows must not call the receipt
/// or generic reversal seam. Every example owns a fresh org; established fixtures remain unchanged.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PaymentLifecycleEvidenceTests(PostgresFixture fixture)
{
    private readonly Guid _tenant = UuidV7.NewId();
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();
    private static readonly DateOnly ReceiptDate = new(2026, 2, 3);
    private static readonly DateOnly ReturnDate = new(2026, 3, 5);

    [Theory]
    [InlineData(600, 600, 0, 400)]
    [InlineData(1000, 1000, 0, 0)]
    [InlineData(1200, 1000, 200, -200)]
    public async Task Receipt_books_the_bank_immediately_with_exact_attributed_lines(
        decimal amount, decimal applied, decimal excess, decimal tenantBalance)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, 1000m, ct);
        var receipt = await Post(scope, Receipt(scope, amount), ct);

        var expected = new List<LineView>
        {
            new(AccountCodes.TrustBank(scope.TrustBankId), amount, null, EntryBasis.Both,
                null, null, null, null, scope.TrustBankId),
            new(AccountCodes.TenantReceivable, null, applied, EntryBasis.Accrual,
                _tenant, _owner, _property, null, null),
            new(AccountCodes.OwnerEquity, null, applied, EntryBasis.Cash,
                null, _owner, _property, null, scope.TrustBankId),
        };
        if (excess > 0)
        {
            expected.Add(new(AccountCodes.TenantPrepayments, null, excess, EntryBasis.Both,
                _tenant, null, null, null, scope.TrustBankId));
        }

        var lines = await ReadLinesAsync(scope, receipt, ct);
        lines.ShouldBe(expected, ignoreOrder: true);
        AssertBalanced(lines);
        await AssertBalances(scope, amount, tenantBalance, ct);
        await AssertClean(scope, ct);
    }

    [Theory]
    [InlineData(1000, 1000, 0)]
    [InlineData(970, 970, 30)]
    public async Task Gross_or_net_receipt_cannot_represent_a_1000_collection_with_a_30_processor_fee(
        decimal receiptAmount, decimal book, decimal tenantBalance)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, 1000m, ct);
        await Post(scope, Receipt(scope, receiptAmount), ct);

        // A hypothetical statement contains only a 970 deposit after a 30 fee. These are illustrative
        // amounts, not provider pricing. Gross posting overstates bank by 30; net posting leaves 30
        // owed by the tenant. The real engine/invariants alone cannot determine who bears that fee.
        await AssertBalances(scope, book, tenantBalance, ct);
        await scope.RunAsync(async () =>
        {
            var bankLines = await scope.Db.Set<JournalLine>()
                .Where(l => l.AccountClass == AccountClass.TrustBank).Select(l => l.Id).ToArrayAsync(ct);
            await new ApplyClearancesHandler(scope.Db, scope.Tenant).Handle(new ApplyClearances(bankLines), ct);
            var reconciliation = await new StartReconciliationHandler(scope.Db)
                .Handle(new StartReconciliation(scope.TrustBankId, 2026, 2, 970m), ct);
            reconciliation.Difference.ShouldBe(receiptAmount == 1000m ? -30m : 0m);
            if (receiptAmount == 1000m)
            {
                await Should.ThrowAsync<ReconciliationUnbalancedException>(() =>
                    new FinalizeReconciliationHandler(scope.Db, scope.Actor)
                        .Handle(new FinalizeReconciliation(reconciliation.Id), ct));
            }
        }, ct);
        await AssertClean(scope, ct);
    }

    [Fact]
    public async Task Unconsumed_excess_receipt_can_be_mirrored_in_a_later_open_period()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, 1000m, ct);
        var receipt = await Post(scope, Receipt(scope, 1200m), ct);
        var original = await ReadLinesAsync(scope, receipt, ct);
        await scope.RunAsync(() => Periods(scope).CloseAsync(2026, 2, ct), ct);

        Guid reversed = default;
        await scope.RunAsync(async () => reversed = await Reversal(scope)
            .ReverseAsync(receipt, "simulated bank debit confirmed", ReturnDate, "sim:return:1", ct), ct);
        var mirrored = await ReadLinesAsync(scope, reversed, ct);
        mirrored.ShouldBe(original.Select(l => l with { Debit = l.Credit, Credit = l.Debit }), ignoreOrder: true);
        AssertBalanced(mirrored);
        await scope.RunAsync(async () =>
        {
            var entry = await scope.Db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == reversed, ct);
            entry.ReversesEntryId.ShouldBe(receipt);
            entry.EntryDate.ShouldBe(ReturnDate);
            entry.SourceRef.ShouldBe("sim:return:1");
        }, ct);
        await AssertBalances(scope, 0m, 1000m, ct);
        await AssertClean(scope, ct);
    }

    [Fact]
    public async Task Generic_return_after_excess_was_applied_balances_but_violates_the_liability_invariant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, 1000m, ct);
        var receipt = await Post(scope, Receipt(scope, 1200m), ct);
        await Charge(scope, 200m, ct);
        await Post(scope, new PrepaymentApplied(_tenant, _property, _owner, new Money(200m),
            new DateOnly(2026, 2, 4), scope.TrustBankId, "consume excess"), ct);
        await AssertClean(scope, ct);

        Guid reversed = default;
        await scope.RunAsync(async () => reversed = await Reversal(scope)
            .ReverseAsync(receipt, "unsafe generic return demonstration", ReturnDate, ct), ct);
        AssertBalanced(await ReadLinesAsync(scope, reversed, ct));
        await AssertBalances(scope, 0m, 1200m, ct);
        await scope.RunAsync(async () =>
        {
            var checks = new InvariantChecks(scope.Db);
            (await checks.CheckTrustEquationAsync(ct)).ShouldBeEmpty();
            var violations = await checks.CheckCoreAsync(ct);
            violations.ShouldContain(v => v.Invariant == "I4");
            violations.ShouldAllBe(v => v.Invariant == "I4");
        }, ct);
    }

    [Fact]
    public async Task Receipt_and_return_rejected_by_closed_period_leave_no_partial_journal_effect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, 1000m, ct);
        var receipt = await Post(scope, Receipt(scope, 600m), ct);
        await scope.RunAsync(() => Periods(scope).CloseAsync(2026, 2, ct), ct);

        await Should.ThrowAsync<PeriodClosedException>(() => Post(scope, Receipt(scope, 400m, "sim:receipt:2"), ct));
        await Should.ThrowAsync<PeriodClosedException>(() => scope.RunAsync(() =>
            Reversal(scope).ReverseAsync(receipt, "locked date", ReceiptDate, ct), ct));
        await scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>().CountAsync(ct)).ShouldBe(2), ct);
        await AssertBalances(scope, 600m, 400m, ct);
        await AssertClean(scope, ct);
    }

    [Fact]
    public async Task Reconciled_bank_month_rejects_receipt_and_return_without_partial_effect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, 1000m, ct);
        var receipt = await Post(scope, Receipt(scope, 600m), ct);
        await scope.RunAsync(async () =>
        {
            var bankLines = await scope.Db.Set<JournalLine>()
                .Where(l => l.AccountClass == AccountClass.TrustBank).Select(l => l.Id).ToArrayAsync(ct);
            await new ApplyClearancesHandler(scope.Db, scope.Tenant).Handle(new ApplyClearances(bankLines), ct);
            var reconciliation = await new StartReconciliationHandler(scope.Db)
                .Handle(new StartReconciliation(scope.TrustBankId, 2026, 2, 600m), ct);
            await new FinalizeReconciliationHandler(scope.Db, scope.Actor)
                .Handle(new FinalizeReconciliation(reconciliation.Id), ct);
        }, ct);

        await Should.ThrowAsync<AccountPeriodLockedException>(() => Post(scope, Receipt(scope, 400m, "sim:receipt:2"), ct));
        await Should.ThrowAsync<AccountPeriodLockedException>(() => scope.RunAsync(() =>
            Reversal(scope).ReverseAsync(receipt, "locked bank month", ReceiptDate, ct), ct));
        await scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>().CountAsync(ct)).ShouldBe(2), ct);
        await AssertBalances(scope, 600m, 400m, ct);
        await AssertClean(scope, ct);
    }

    [Theory]
    [InlineData(600)]
    [InlineData(700)]
    public async Task Source_ref_duplicates_throw_for_identical_and_changed_amounts_without_posting_again(decimal replayAmount)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, 1000m, ct);
        var receipt = await Post(scope, Receipt(scope, 600m), ct);
        var error = await Should.ThrowAsync<DuplicateSourceRefException>(() =>
            Post(scope, Receipt(scope, replayAmount), ct));
        error.ExistingEntryId.ShouldBe(receipt);
        await scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>().CountAsync(ct)).ShouldBe(2), ct);
        await AssertBalances(scope, 600m, 400m, ct);
        await AssertClean(scope, ct);
    }

    private Task<OrgScope> NewScope(CancellationToken ct) => ProvisionedScopeAsync(
        fixture, ct, owners: [_owner], tenants: [_tenant], properties: [_property]);

    private Task<Guid> Charge(OrgScope scope, decimal amount, CancellationToken ct) => Post(scope,
        new RentCharged(_tenant, _property, _owner, null, new Money(amount), new DateOnly(2026, 2, 1), "rent"), ct);

    private PaymentReceived Receipt(OrgScope scope, decimal amount, string sourceRef = "sim:receipt:1") =>
        new(_tenant, _property, _owner, new Money(amount), ReceiptDate, PaymentMethod.Ach,
            scope.TrustBankId, "simulated settlement evidence", sourceRef);

    private static async Task<Guid> Post(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private async Task AssertBalances(OrgScope scope, decimal bank, decimal tenant, CancellationToken ct) =>
        await scope.RunAsync(async () =>
        {
            (await new GetBankRegisterHandler(scope.Db).Handle(new GetBankRegister(scope.TrustBankId), ct))
                .Totals.Book.ShouldBe(bank);
            (await new GetTenantLedgerHandler(scope.Db).Handle(new GetTenantLedger(_tenant), ct))
                .Balance.ShouldBe(tenant);
        }, ct);

    private static async Task AssertClean(OrgScope scope, CancellationToken ct) =>
        await scope.RunAsync(async () => (await new InvariantChecks(scope.Db).CheckCoreAsync(ct)).ShouldBeEmpty(), ct);

    private static void AssertBalanced(IEnumerable<LineView> lines)
    {
        foreach (var basis in new[] { EntryBasis.Cash, EntryBasis.Accrual })
        {
            var selected = lines.Where(l => l.Basis == basis || l.Basis == EntryBasis.Both).ToArray();
            selected.Sum(l => l.Debit ?? 0m).ShouldBe(selected.Sum(l => l.Credit ?? 0m));
        }
    }
}

