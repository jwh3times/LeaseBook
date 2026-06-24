using CsCheck;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting.Migration;

/// <summary>
/// WP-2 property suite (M7/ADR-020): for any random set of balanced opening positions, posting each
/// via <see cref="IBalanceForward.PostOpeningPositionAsync"/> leaves <c>migration_clearing</c> at
/// $0.00 in BOTH bases and the core invariants hold. A deliberately unbalanced set leaves a non-zero
/// clearing residual equal to the injected gap (the failure path is detectable, not self-canceling).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ClearingInvariantTests(PostgresFixture fixture)
{
    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("LEASEBOOK_PROPERTY_ITER"), out var n) ? n : 20;

    [Fact]
    public async Task Balanced_opening_set_leaves_clearing_at_zero_in_both_bases()
    {
        var ct = TestContext.Current.CancellationToken;

        // Generate 1–5 owner-equity opening positions (credit); matching trust-bank debits make the
        // set self-balancing. All basis = Both so the clearing legs net in every basis projection.
        var genPosition = Gen.Int[1, 2_500_000].Select(cents => new Money(cents / 100m));

        await genPosition.Array[1, 5].SampleAsync(
            amounts => RunBalancedCaseAsync(amounts, ct), iter: Iterations);
    }

    [Fact]
    public async Task Unbalanced_opening_set_leaves_clearing_equal_to_injected_gap()
    {
        var ct = TestContext.Current.CancellationToken;

        // One owner-equity credit with NO matching trust-bank debit → clearing accumulates a residual
        // equal to the credit amount (the gap). The residual must be detectable as non-zero.
        var genGap = Gen.Int[1, 2_500_000].Select(cents => new Money(cents / 100m));

        await genGap.SampleAsync(
            gap => RunUnbalancedCaseAsync(gap, ct), iter: Iterations);
    }

    private async Task RunBalancedCaseAsync(Money[] amounts, CancellationToken ct)
    {
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var balanceForward = (IBalanceForward)Events(scope);

        // Generate one owner per amount so owner/trust-bank pair posts cleanly.
        var owners = amounts.Select(_ => UuidV7.NewId()).ToArray();
        await EnsureDirectoryAsync(fixture, scope, ct, owners: owners);

        var cutover = new DateOnly(2026, 6, 30);

        await scope.RunAsync(async () =>
        {
            for (var i = 0; i < amounts.Length; i++)
            {
                var owner = owners[i];
                var amount = amounts[i];

                // Owner-equity credit (the owner's opening balance)
                await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                    AccountCodes.OwnerEquity, Debit: null, Credit: amount, EntryBasis.Both,
                    cutover, $"opening:{cutover:yyyy-MM-dd}:owner-equity={owner}",
                    OwnerId: owner, BankAccountId: scope.TrustBankId), ct);

                // Matching trust-bank debit (the bank balance) — this balances the clearing account
                // because each PostOpeningPositionAsync self-balances against clearing:
                //   equity credit → clearing debit (net: clearing debit +amount)
                //   trust debit   → clearing credit (net: clearing debit -amount → 0)
                await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                    AccountCodes.TrustBank(scope.TrustBankId), Debit: amount, Credit: null, EntryBasis.Both,
                    cutover, $"opening:{cutover:yyyy-MM-dd}:trust-bank={owner}",
                    BankAccountId: scope.TrustBankId), ct);
            }

            // I5: clearing nets to $0 in both bases after a balanced set.
            var clearingViolations = await new InvariantChecks(scope.Db).CheckMigrationClearingBalancedAsync(ct);
            clearingViolations.ShouldBeEmpty(
                string.Join("; ", clearingViolations.Select(v => $"{v.Invariant}:{v.Detail}")));

            // Core invariants also hold.
            var allViolations = await new InvariantChecks(scope.Db).CheckCoreAsync(ct);
            allViolations.ShouldBeEmpty(
                string.Join("; ", allViolations.Select(v => $"{v.Invariant}:{v.Detail}")));
        }, ct);
    }

    private async Task RunUnbalancedCaseAsync(Money gap, CancellationToken ct)
    {
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var balanceForward = (IBalanceForward)Events(scope);

        var owner = UuidV7.NewId();
        await EnsureDirectoryAsync(fixture, scope, ct, owners: [owner]);

        var cutover = new DateOnly(2026, 6, 30);

        await scope.RunAsync(async () =>
        {
            // Post only the owner-equity credit — no matching bank debit.
            // Each PostOpeningPositionAsync self-balances within the entry (real leg + clearing contra).
            // But without the paired trust-bank posting, clearing accumulates a net debit = gap.
            await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.OwnerEquity, Debit: null, Credit: gap, EntryBasis.Both,
                cutover, $"opening:{cutover:yyyy-MM-dd}:owner-equity-unbalanced={owner}",
                OwnerId: owner, BankAccountId: scope.TrustBankId), ct);

            // The clearing account now has a net debit (the gap). I5 should report a violation.
            var clearingViolations = await new InvariantChecks(scope.Db).CheckMigrationClearingBalancedAsync(ct);
            clearingViolations.ShouldNotBeEmpty("an unbalanced set must leave a detectable clearing residual");

            // The violation detail should mention the gap amount.
            clearingViolations[0].Detail.ShouldContain(gap.Amount.ToString("0.00"));
        }, ct);
    }
}
