using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LeaseBook.Tests.Accounting.Migration;

[Collection(nameof(DatabaseCollection))]
public sealed class OpeningBalanceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Chart_of_accounts_provisions_a_migration_clearing_account()
    {
        var scope = await AccountingTestHarness.ProvisionedScopeAsync(fixture, TestContext.Current.CancellationToken);
        await scope.RunAsync(async () =>
        {
            var exists = await scope.Db.Set<Account>()
                .AnyAsync(a => a.Code == AccountCodes.MigrationClearing && a.Class == AccountClass.MigrationClearing);
            exists.ShouldBeTrue();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Opening_position_posts_a_real_leg_and_an_equal_clearing_contra()
    {
        var owner = UuidV7.NewId();
        var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, TestContext.Current.CancellationToken, owners: [owner]);
        var balanceForward = (IBalanceForward)AccountingTestHarness.Events(scope);

        Guid entryId = default;
        await scope.RunAsync(async () =>
        {
            entryId = await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.OwnerEquity, Debit: null, Credit: new Money(13_665.50m), EntryBasis.Both,
                new DateOnly(2026, 6, 30), "opening:2026-06-30:owner-equity=" + owner,
                OwnerId: owner, BankAccountId: scope.TrustBankId),
                TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        var lines = await AccountingTestHarness.ReadLinesAsync(scope, entryId, TestContext.Current.CancellationToken);
        lines.Count.ShouldBe(2);
        lines.ShouldContain(l => l.Code == AccountCodes.OwnerEquity && l.Credit == 13_665.50m && l.OwnerId == owner);
        lines.ShouldContain(l => l.Code == AccountCodes.MigrationClearing && l.Debit == 13_665.50m);
    }
}
