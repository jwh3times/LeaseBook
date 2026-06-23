using LeaseBook.Modules.Directory.Features.Reporting;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-02: rent roll (report #6) — pure Directory data, EF LINQ over Directory's own tables (ADR-007).
/// Asserts against the seeded units/tenants from DemoDirectorySeed (20 units, 7 occupied). No Accounting
/// tables are touched.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RentRollTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RentRoll_returns_all_non_system_units()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var result = await DispatchAsync((s, c) => s.Query(new GetRentRoll(), c), ct);

        // 20 non-system units seeded (data.jsx: p1=4, p2=1, p3=6, p4=3, p5=4, p6=2).
        result.Rows.Count.ShouldBe(20);
    }

    [Fact]
    public async Task RentRoll_occupied_units_carry_tenant_name_and_lease_rent()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var result = await DispatchAsync((s, c) => s.Query(new GetRentRoll(), c), ct);

        // 7 occupied units from the seed.
        var occupied = result.Rows.Where(r => r.Status == "occupied").ToList();
        occupied.Count.ShouldBe(7);

        // Occupied rows always have a tenant name.
        occupied.ShouldAllBe(r => r.Tenant != null);

        // Focal tenant: Jasmine Carter at 412 Oakmont Ave #2B, lease rent 1450.
        var jasmine = result.Rows.SingleOrDefault(r => r.Tenant == "Jasmine Carter");
        jasmine.ShouldNotBeNull();
        jasmine!.Property.ShouldBe("412 Oakmont Ave");
        jasmine.Rent.ShouldBe(1450m);
        jasmine.Status.ShouldBe("occupied");
    }

    [Fact]
    public async Task RentRoll_vacant_units_carry_no_tenant_name()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var result = await DispatchAsync((s, c) => s.Query(new GetRentRoll(), c), ct);

        var vacant = result.Rows.Where(r => r.Status == "vacant").ToList();
        // 13 vacant units (20 − 7 occupied).
        vacant.Count.ShouldBe(13);
        vacant.ShouldAllBe(r => r.Tenant == null);
    }

    [Fact]
    public async Task RentRoll_all_rows_carry_a_property_address()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var result = await DispatchAsync((s, c) => s.Query(new GetRentRoll(), c), ct);

        result.Rows.ShouldAllBe(r => !string.IsNullOrWhiteSpace(r.Property));
    }

    [Fact]
    public async Task RentRoll_system_rows_do_not_surface()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var result = await DispatchAsync((s, c) => s.Query(new GetRentRoll(), c), ct);

        // System tenant rows (AggDepO1..8, statement-only) must never appear as Tenant names.
        result.Rows.ShouldNotContain(r => r.Tenant != null && r.Tenant.StartsWith("Deposit aggregate"));
        result.Rows.ShouldNotContain(r => r.Tenant == "T. Okonkwo" || r.Tenant == "T. Liu");
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
