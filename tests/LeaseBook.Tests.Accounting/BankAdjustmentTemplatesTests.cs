using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-03 (M4 / ADR-014): the three bank-adjustment templates post balanced, owner-isolated lines and keep
/// the core invariants — crucially the trust equation — clean. Each is modeled as a movement of the PM's
/// own held funds (pm_income tagged to the bank), never owner/deposit money.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class BankAdjustmentTemplatesTests(PostgresFixture fixture)
{
    [Fact]
    public async Task BankFeeCharged_reduces_the_bank_and_held_pm_fees()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        var id = await PostAsync(scope, new BankFeeCharged(new Money(50m), D(1), scope.TrustBankId, "wire fee"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(2);
        Debit(lines, AccountCodes.PmIncome).Debit.ShouldBe(50m);       // held PM fees ↓
        Credit(lines, AccountCodes.TrustBank(scope.TrustBankId)).Credit.ShouldBe(50m); // bank ↓
        lines.ShouldAllBe(l => l.OwnerId == null);
        await AssertBalancedAndCleanAsync(scope, lines, ct);
    }

    [Fact]
    public async Task InterestEarned_raises_the_bank_and_held_pm_fees()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        var id = await PostAsync(scope, new InterestEarned(new Money(12.34m), D(2), scope.TrustBankId, "interest"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(2);
        Debit(lines, AccountCodes.TrustBank(scope.TrustBankId)).Debit.ShouldBe(12.34m); // bank ↑
        Credit(lines, AccountCodes.PmIncome).Credit.ShouldBe(12.34m);                   // held PM fees ↑
        lines.ShouldAllBe(l => l.OwnerId == null);
        await AssertBalancedAndCleanAsync(scope, lines, ct);
    }

    [Fact]
    public async Task TrustTransfer_moves_cash_and_held_fees_together_in_four_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        var id = await PostAsync(scope, new TrustTransfer(
            new Money(500m), D(3), scope.TrustBankId, scope.OperatingBankId, "fund operating"), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        lines.Count.ShouldBe(4);
        Debit(lines, AccountCodes.PmOperatingBank(scope.OperatingBankId)).Debit.ShouldBe(500m); // to-bank cash ↑
        Credit(lines, AccountCodes.TrustBank(scope.TrustBankId)).Credit.ShouldBe(500m);          // from-bank cash ↓

        // The held-fee attribution moves with the cash: debit (↓) on the source bank, credit (↑) on the dest.
        var pmDebit = lines.Single(l => l.Code == AccountCodes.PmIncome && l.Debit is not null);
        pmDebit.BankAccountId.ShouldBe(scope.TrustBankId);
        var pmCredit = lines.Single(l => l.Code == AccountCodes.PmIncome && l.Credit is not null);
        pmCredit.BankAccountId.ShouldBe(scope.OperatingBankId);

        lines.ShouldAllBe(l => l.OwnerId == null);
        await AssertBalancedAndCleanAsync(scope, lines, ct);
    }

    private static async Task<Guid> PostAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private static async Task AssertBalancedAndCleanAsync(OrgScope scope, IReadOnlyList<LineView> lines, CancellationToken ct)
    {
        foreach (var basis in new[] { EntryBasis.Cash, EntryBasis.Accrual })
        {
            var debits = lines.Where(l => l.Basis == basis || l.Basis == EntryBasis.Both).Sum(l => l.Debit ?? 0m);
            var credits = lines.Where(l => l.Basis == basis || l.Basis == EntryBasis.Both).Sum(l => l.Credit ?? 0m);
            debits.ShouldBe(credits, $"entry should balance in {basis} basis");
        }

        IReadOnlyList<InvariantViolation> violations = [];
        await scope.RunAsync(async () => violations = await new InvariantChecks(scope.Db).CheckCoreAsync(ct), ct);
        violations.ShouldBeEmpty(string.Join("; ", violations.Select(v => $"{v.Invariant}:{v.Detail}")));
    }

    private static LineView Debit(IEnumerable<LineView> lines, string code) =>
        lines.Single(l => l.Code == code && l.Debit is not null);

    private static LineView Credit(IEnumerable<LineView> lines, string code) =>
        lines.Single(l => l.Code == code && l.Credit is not null);

    private static DateOnly D(int day) => new(2026, 2, day);
}
