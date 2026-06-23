using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
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
}
