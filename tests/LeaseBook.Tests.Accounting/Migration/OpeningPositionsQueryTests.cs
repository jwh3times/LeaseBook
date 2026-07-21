using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Migration;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting.Migration;

/// <summary>
/// WP-7 Half A read side: GetOpeningPositions returns one row per OpeningBalance entry with the
/// real (non-clearing) leg, marks reversed entries, and excludes BalanceForward and EntryVoided.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OpeningPositionsQueryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Returns_real_leg_marks_reversed_and_excludes_voids_and_batched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var owner = UuidV7.NewId();
        await EnsureDirectoryAsync(fixture, scope, ct, owners: [owner]);
        var cutover = new DateOnly(2026, 6, 30);
        var balanceForward = (IBalanceForward)Events(scope);

        await scope.RunAsync(async () =>
        {
            // Two opening positions: an owner-equity credit and a trust-bank debit.
            var equityRef = $"opening:{cutover:yyyy-MM-dd}:owner-equity={owner}";
            var equityId = await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.OwnerEquity, Debit: null, Credit: new Money(500m), EntryBasis.Both,
                cutover, equityRef, OwnerId: owner, BankAccountId: scope.TrustBankId), ct);
            await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.TrustBank(scope.TrustBankId), Debit: new Money(500m), Credit: null,
                EntryBasis.Both, cutover, $"opening:{cutover:yyyy-MM-dd}:bank={scope.TrustBankId}",
                BankAccountId: scope.TrustBankId), ct);

            // Void the equity entry — it must come back IsReversed=true; the void itself never appears.
            await Reversal(scope).ReverseAsync(equityId, "test void", cutover, ct);

            var handler = new GetOpeningPositionsHandler(scope.Db);
            var result = await handler.Handle(new GetOpeningPositions(), ct);

            result.Entries.Count.ShouldBe(2);
            var equity = result.Entries.Single(e => e.SourceRef == equityRef);
            equity.EntryId.ShouldBe(equityId);
            equity.IsReversed.ShouldBeTrue();
            equity.AccountCode.ShouldBe(AccountCodes.OwnerEquity);
            equity.Debit.ShouldBeNull();
            equity.Credit.ShouldBe(500m);
            equity.Basis.ShouldBe("both");
            equity.OwnerId.ShouldBe(owner);
            equity.EntryDate.ShouldBe(cutover);

            var bank = result.Entries.Single(e => e.SourceRef.StartsWith($"opening:{cutover:yyyy-MM-dd}:bank="));
            bank.IsReversed.ShouldBeFalse();
            bank.Debit.ShouldBe(500m);
        }, ct);
    }
}
