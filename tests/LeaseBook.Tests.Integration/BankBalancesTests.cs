using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// M5-prep Task 1: <see cref="GetBankBalances"/> must report a per-account <c>UnclearedCount</c>.
/// Golden-locked against the demo seed: Operating Trust has exactly 3 uncleared items.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class BankBalancesTests(PostgresFixture fixture)
{
    [Fact]
    public async Task BankBalances_report_per_account_uncleared_counts()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        BankBalancesResponse balances = null!;
        await executor.RunAsync(DemoSeeder.DemoOrgId,
            async () => balances = await sender.Query(new GetBankBalances(), ct), ct);

        // Golden-locked (M4 GoldenFileTests): Operating Trust has exactly 3 uncleared items.
        var operatingTrust = balances.Rows.Single(r => r.Name == "Operating Trust");
        operatingTrust.UnclearedCount.ShouldBe(3);
        // book = cleared + uncleared net holds row-by-row.
        operatingTrust.Uncleared.ShouldBe(operatingTrust.Book - operatingTrust.Cleared);
    }
}
