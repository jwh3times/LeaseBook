using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-03: directory CRUD + the enriched list/detail reads. Drives the real CQRS pipeline through the
/// host DI in an org transaction. The tenant list/detail balance is proven against a hand-posted charge
/// (the golden-seed proof is WP-06); pagination, free-text filter, system-row exclusion (P40/M2-E2) and
/// cross-org isolation are all checked.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class DirectoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Owner_create_list_detail_round_trips()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        var id = await DispatchAsync(orgId, (s, c) =>
            s.Send(new CreateOwner("Hargrove Family Trust", "HF", "t@h.example", null, 800, 500m), c), ct);

        var list = await DispatchAsync(orgId, (s, c) => s.Query(new ListOwners(null, null, null, null), c), ct);
        list.Total.ShouldBe(1);
        list.Items.ShouldContain(o => o.Id == id && o.Name == "Hargrove Family Trust");

        var detail = await DispatchAsync(orgId, (s, c) => s.Query(new GetOwnerDetail(id), c), ct);
        detail.ShouldNotBeNull();
        detail.DefaultMgmtFeeBps.ShouldBe(800);
        detail.ReserveAmount.ShouldBe(500m);
    }

    [Fact]
    public async Task Tenant_list_and_detail_carry_the_ledger_balance()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        Guid ownerId = default, propertyId = default, unitId = default, tenantId = default;
        await DispatchScopeAsync(orgId, async (s, sp) =>
        {
            ownerId = await s.Send(new CreateOwner("Owner", null, null, null, 800, 0m), ct);
            propertyId = await s.Send(new CreateProperty(ownerId, "412 Oakmont Ave", "Asheville", "NC", "28801", null), ct);
            unitId = await s.Send(new CreateUnit(propertyId, "#2B", 1450m, "occupied"), ct);
            tenantId = await s.Send(new CreateTenant("Jasmine Carter", null, null, "current"), ct);
            await s.Send(new CreateLease(tenantId, unitId, new DateOnly(2025, 6, 1), new DateOnly(2026, 5, 31), 1450m, 1450m, "active"), ct);

            // Hand-post one rent charge through the engine so the tenant nets 1450 (no payment).
            await sp.GetRequiredService<IChartOfAccounts>().ProvisionAsync([], ct);
            await sp.GetRequiredService<IAccountingEvents>().PostAsync(
                new RentCharged(tenantId, propertyId, ownerId, unitId, new Money(1450m), new DateOnly(2026, 2, 1), "rent"), ct);
        }, ct);

        var list = await DispatchAsync(orgId, (s, c) => s.Query(new ListTenants(null, null, null, null), c), ct);
        var row = list.Items.ShouldHaveSingleItem();
        row.DisplayName.ShouldBe("Jasmine Carter");
        row.UnitLabel.ShouldBe("#2B");
        row.Rent.ShouldBe(1450m);
        row.Balance.ShouldBe(1450m);

        var detail = await DispatchAsync(orgId, (s, c) => s.Query(new GetTenantDetail(tenantId), c), ct);
        detail.ShouldNotBeNull();
        detail.Balance.ShouldBe(1450m);
        detail.UnitLabel.ShouldBe("#2B");
        detail.PropertyAddress.ShouldBe("412 Oakmont Ave");
        detail.OwnerName.ShouldBe("Owner");
        detail.Lease.ShouldNotBeNull();
        detail.Lease.Rent.ShouldBe(1450m);
    }

    [Fact]
    public async Task Owner_list_paginates_and_filters()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await DispatchScopeAsync(orgId, async (s, _) =>
        {
            await s.Send(new CreateOwner("Alpha Holdings", null, null, null, null, 0m), ct);
            await s.Send(new CreateOwner("Bravo Trust", null, null, null, null, 0m), ct);
            await s.Send(new CreateOwner("Charlie LLC", null, null, null, null, 0m), ct);
        }, ct);

        var firstPage = await DispatchAsync(orgId, (s, c) => s.Query(new ListOwners(1, 2, null, null), c), ct);
        firstPage.Total.ShouldBe(3);
        firstPage.Items.Count.ShouldBe(2);
        firstPage.Items[0].Name.ShouldBe("Alpha Holdings"); // default sort by name asc

        var secondPage = await DispatchAsync(orgId, (s, c) => s.Query(new ListOwners(2, 2, null, null), c), ct);
        secondPage.Items.Count.ShouldBe(1);

        var filtered = await DispatchAsync(orgId, (s, c) => s.Query(new ListOwners(null, null, "bravo", null), c), ct);
        filtered.Total.ShouldBe(1);
        filtered.Items[0].Name.ShouldBe("Bravo Trust");
    }

    [Fact]
    public async Task System_rows_are_excluded_from_lists()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await DispatchScopeAsync(orgId, async (s, sp) =>
        {
            await s.Send(new CreateOwner("Real Owner", null, null, null, null, 0m), ct);
            var db = sp.GetRequiredService<AppDbContext>();
            db.Set<Owner>().Add(new Owner { Id = UuidV7.NewId(), Name = "All other owners", IsSystem = true });
            db.Set<Tenant>().Add(new Tenant { Id = UuidV7.NewId(), DisplayName = "Aggregate", Status = TenantStatus.Current, IsSystem = true });
            await db.SaveChangesAsync(ct);
        }, ct);

        var owners = await DispatchAsync(orgId, (s, c) => s.Query(new ListOwners(null, null, null, null), c), ct);
        owners.Total.ShouldBe(1);
        owners.Items.ShouldNotContain(o => o.Name == "All other owners");

        var tenants = await DispatchAsync(orgId, (s, c) => s.Query(new ListTenants(null, null, null, null), c), ct);
        tenants.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Lists_are_isolated_across_orgs()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = await NewOrgAsync(ct);
        var orgB = await NewOrgAsync(ct);

        await DispatchAsync(orgA, (s, c) => s.Send(new CreateOwner("A Owner", null, null, null, null, 0m), c), ct);

        var bOwners = await DispatchAsync(orgB, (s, c) => s.Query(new ListOwners(null, null, null, null), c), ct);
        bOwners.Total.ShouldBe(0);
        bOwners.Items.ShouldBeEmpty();
    }

    private async Task<Guid> NewOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Directory Org {orgId:N}" });
        await migratorDb.SaveChangesAsync(ct);
        return orgId;
    }

    private async Task DispatchScopeAsync(Guid orgId, Func<ISender, IServiceProvider, Task> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        await executor.RunAsync(orgId, () => work(scope.ServiceProvider.GetRequiredService<ISender>(), scope.ServiceProvider), ct);
    }

    private async Task<T> DispatchAsync<T>(Guid orgId, Func<ISender, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        T result = default!;
        await DispatchScopeAsync(orgId, async (s, _) => result = await work(s, ct), ct);
        return result;
    }
}
