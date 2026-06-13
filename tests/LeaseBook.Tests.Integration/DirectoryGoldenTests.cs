using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Search;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-06 golden join: after the demo seed, the directory reads return the M1 golden figures <b>now with
/// names</b> — owners/tenants resolve to "Hargrove Family Trust" / "Jasmine Carter", not bare GUIDs — and
/// the property page aggregates its units/tenants/owner. System rows never surface. The financial figures
/// flow through the Accounting ports (so they tie to the cent against <c>GoldenFileTests</c>).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class DirectoryGoldenTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Owner_list_returns_the_eight_named_owners_with_golden_balances()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var owners = await DispatchAsync((s, c) => s.Query(new ListOwners(null, 200, null, null), c), ct);

        owners.Total.ShouldBe(8); // 8 listed owners; the AggregateOwners roll-up is hidden (P40)
        owners.Items.ShouldNotContain(o => o.Name == "All other owners");

        var hargrove = owners.Items.Single(o => o.Name == "Hargrove Family Trust");
        hargrove.Operating.ShouldBe(14_820.50m);
        hargrove.Deposits.ShouldBe(8_400.00m);
    }

    [Fact]
    public async Task Tenant_list_returns_the_seven_named_tenants_with_golden_balances()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var tenants = await DispatchAsync((s, c) => s.Query(new ListTenants(null, 200, null, null), c), ct);

        tenants.Total.ShouldBe(7); // 7 listed tenants; deposit-aggregate/statement system rows hidden

        var jasmine = tenants.Items.Single(t => t.DisplayName == "Jasmine Carter");
        jasmine.UnitLabel.ShouldBe("#2B");
        jasmine.Rent.ShouldBe(1450m);
        jasmine.Balance.ShouldBe(1450m);

        tenants.Items.Single(t => t.DisplayName == "The Mercer Family").Balance.ShouldBe(-75m);
        tenants.Items.Single(t => t.DisplayName == "Lena Vasquez").Balance.ShouldBe(2820m);
        tenants.Items.Single(t => t.DisplayName == "Devon Pryor").Balance.ShouldBe(0m);
    }

    [Fact]
    public async Task Property_detail_aggregates_units_tenants_and_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var detail = await DispatchAsync((s, c) => s.Query(new GetPropertyDetail(DemoIds.P1), c), ct);

        detail.ShouldNotBeNull();
        detail.Address.ShouldBe("412 Oakmont Ave");
        detail.OwnerName.ShouldBe("Hargrove Family Trust");
        detail.Units.Count.ShouldBe(4); // #2B, #1A occupied + 2 vacant fillers
        detail.Units.Count(u => u.Status == "occupied").ShouldBe(2);
        detail.Tenants.Select(t => t.DisplayName).ShouldBe(["Devon Pryor", "Jasmine Carter"], ignoreOrder: true);
    }

    [Fact]
    public async Task Search_finds_the_focal_tenant_first()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var results = await DispatchAsync((s, c) => s.Query(new Search("carter", null), c), ct);

        results[0].Type.ShouldBe("tenant");
        results[0].Label.ShouldBe("Jasmine Carter");
    }

    private async Task<T> DispatchAsync<T>(Func<ISender, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        T result = default!;
        await executor.RunAsync(DemoSeeder.DemoOrgId, async () => result = await work(sender, ct), ct);
        return result;
    }
}
