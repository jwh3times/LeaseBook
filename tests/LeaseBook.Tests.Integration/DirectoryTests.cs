using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Dashboard;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Reporting;
using LeaseBook.Modules.Directory.Features.Search;
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
            unitId = await s.Send(new CreateUnit(propertyId, "#2B", 1450m, "available"), ct);
            tenantId = await s.Send(new CreateTenant("Jasmine Carter", null, null, "current"), ct);
            await s.Send(new CreateLease(
                tenantId,
                unitId,
                new DateOnly(2025, 6, 1),
                DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1),
                1450m,
                1450m,
                "active"), ct);

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
    public async Task Tenant_can_be_delinquent_and_hold_unapplied_credit_without_changing_lifecycle()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Guid tenantId = default;
        await DispatchScopeAsync(orgId, async (sender, services) =>
        {
            var ownerId = await sender.Send(new CreateOwner("Owner", null, null, null, 800, 0m), ct);
            var propertyId = await sender.Send(
                new CreateProperty(ownerId, "19 Standing Way", "Raleigh", "NC", null, null), ct);
            var unitId = await sender.Send(new CreateUnit(propertyId, "A", 100m, "available"), ct);
            tenantId = await sender.Send(new CreateTenant("Standing Tenant", null, null, "current"), ct);
            var bank = await sender.Send(new CreateBankAccount("Operating Trust", null, "1001", "trust"), ct);

            var events = services.GetRequiredService<IAccountingEvents>();
            await events.PostAsync(new RentCharged(
                tenantId,
                propertyId,
                ownerId,
                unitId,
                new Money(100m),
                today.AddDays(-15),
                "Past-due rent",
                DueDate: today.AddDays(-10)), ct);
            await events.PostAsync(new PrepaymentReceived(
                tenantId,
                propertyId,
                ownerId,
                new Money(25m),
                today.AddDays(-5),
                bank.Id,
                "Future rent credit"), ct);
        }, ct);

        var detail = await DispatchAsync(orgId, (sender, c) => sender.Query(new GetTenantDetail(tenantId), c), ct);

        detail.ShouldNotBeNull();
        detail.LifecycleStatus.ShouldBe("current");
        detail.FinancialStanding.DelinquentBalance.ShouldBe(100m);
        detail.FinancialStanding.UnappliedCredit.ShouldBe(25m);
        detail.Balance.ShouldBe(75m);
    }

    [Fact]
    public async Task Leased_but_unavailable_unit_is_occupied_and_not_vacant()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Guid propertyId = default;
        await DispatchScopeAsync(orgId, async (sender, _) =>
        {
            var ownerId = await sender.Send(new CreateOwner("Owner", null, null, null, 800, 0m), ct);
            propertyId = await sender.Send(
                new CreateProperty(ownerId, "20 Maintenance Way", "Raleigh", "NC", null, null), ct);
            var unitId = await sender.Send(new CreateUnit(propertyId, "A", 1000m, "unavailable"), ct);
            var tenantId = await sender.Send(new CreateTenant("Maintenance Tenant", null, null, "current"), ct);
            await sender.Send(new CreateLease(
                tenantId,
                unitId,
                today.AddMonths(-1),
                today.AddMonths(1),
                1000m,
                1000m,
                "active"), ct);
        }, ct);

        var units = await DispatchAsync(orgId, (sender, c) => sender.Query(new ListUnits(propertyId), c), ct);
        var kpis = await DispatchAsync(orgId, (sender, c) => sender.Query(new GetDirectoryKpis(), c), ct);

        var unit = units.ShouldHaveSingleItem();
        unit.Occupancy.ShouldBe("occupied");
        unit.Availability.ShouldBe("unavailable");
        kpis.Vacancy.ShouldBe(0);
    }

    [Fact]
    public async Task Tenant_detail_does_not_present_a_future_active_lease_as_current()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Guid propertyId = default;
        Guid tenantId = default;
        await DispatchScopeAsync(orgId, async (s, _) =>
        {
            var ownerId = await s.Send(new CreateOwner("Future Owner", null, null, null, 800, 0m), ct);
            propertyId = await s.Send(
                new CreateProperty(ownerId, "14 Tomorrow Lane", "Raleigh", "NC", null, null), ct);
            var unitId = await s.Send(new CreateUnit(propertyId, "1", 1200m, "available"), ct);
            tenantId = await s.Send(new CreateTenant("Future Resident", null, null, "current"), ct);
            await s.Send(new CreateLease(
                tenantId,
                unitId,
                today.AddDays(30),
                today.AddYears(1),
                1200m,
                1200m,
                "active"), ct);
        }, ct);

        var detail = await DispatchAsync(orgId, (s, c) => s.Query(new GetTenantDetail(tenantId), c), ct);

        detail.ShouldNotBeNull();
        detail.Lease.ShouldBeNull();
        detail.UnitLabel.ShouldBeNull();
        detail.PropertyAddress.ShouldBeNull();
        detail.OwnerId.ShouldBeNull();

        var tenants = await DispatchAsync(orgId, (s, c) => s.Query(new ListTenants(null, null, null, null), c), ct);
        var tenant = tenants.Items.ShouldHaveSingleItem();
        tenant.UnitLabel.ShouldBeNull();
        tenant.Rent.ShouldBe(0m);

        var property = await DispatchAsync(orgId, (s, c) => s.Query(new GetPropertyDetail(propertyId), c), ct);
        property.ShouldNotBeNull();
        property.Tenants.ShouldBeEmpty();

        var rentRoll = await DispatchAsync(orgId, (s, c) => s.Query(new GetRentRoll(), c), ct);
        var unit = rentRoll.Rows.ShouldHaveSingleItem();
        unit.Tenant.ShouldBeNull();
        unit.Rent.ShouldBe(1200m);

        var search = await DispatchAsync(orgId, (s, c) => s.Query(new Search("Future Resident", null), c), ct);
        search.Single(result => result.Type == "tenant").Sublabel.ShouldBe("");
    }

    [Fact]
    public async Task Lease_schedule_includes_an_ended_lease_that_was_effective_during_the_period()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        Guid leaseId = default;
        await DispatchScopeAsync(orgId, async (s, _) =>
        {
            var ownerId = await s.Send(new CreateOwner("Historical Owner", null, null, null, 800, 0m), ct);
            var propertyId = await s.Send(
                new CreateProperty(ownerId, "15 Yesterday Lane", "Raleigh", "NC", null, null), ct);
            var unitId = await s.Send(new CreateUnit(propertyId, "2", 1300m, "available"), ct);
            var tenantId = await s.Send(new CreateTenant("Former Resident", null, null, "past"), ct);
            leaseId = await s.Send(new CreateLease(
                tenantId,
                unitId,
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 6, 30),
                1300m,
                1300m,
                "ended"), ct);
        }, ct);

        var schedule = await DispatchAsync(
            orgId,
            (s, c) => s.Query(new GetActiveLeaseSchedule(2026, 5), c),
            ct);

        schedule.Rows.ShouldHaveSingleItem().LeaseId.ShouldBe(leaseId);
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
            db.Set<Tenant>().Add(new Tenant
            {
                Id = UuidV7.NewId(),
                DisplayName = "Aggregate",
                LifecycleStatus = TenantLifecycleStatus.Current,
                IsSystem = true,
            });
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
        await executor.RunAsSystemAsync(orgId, "test-harness", () => work(scope.ServiceProvider.GetRequiredService<ISender>(), scope.ServiceProvider), ct);
    }

    private async Task<T> DispatchAsync<T>(Guid orgId, Func<ISender, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        T result = default!;
        await DispatchScopeAsync(orgId, async (s, _) => result = await work(s, ct), ct);
        return result;
    }
}
