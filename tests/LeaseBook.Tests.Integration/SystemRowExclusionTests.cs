using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Search;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// Hardening guard (m2_retro known-limitation #2): the system aggregate rows ("All other owners",
/// synthetic deposit-aggregate tenants) must never leak through any directory roster/search read.
/// Behavioral — asserts the rows are absent by id, and that the aggregates actually exist so the test
/// can't pass vacuously.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SystemRowExclusionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task System_aggregate_rows_never_appear_in_roster_or_search()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();

        await executor.RunAsync(DemoSeeder.DemoOrgId, async () =>
        {
            var systemOwnerIds = await db.Set<Owner>().Where(o => o.IsSystem).Select(o => o.Id).ToListAsync(ct);
            var systemTenantIds = await db.Set<Tenant>().Where(t => t.IsSystem).Select(t => t.Id).ToListAsync(ct);
            systemOwnerIds.ShouldNotBeEmpty();   // guard: the aggregates exist
            systemTenantIds.ShouldNotBeEmpty();

            var owners = await sender.Query(new ListOwners(1, 500, null, null), ct);
            owners.Items.Select(i => i.Id).ShouldNotContain(id => systemOwnerIds.Contains(id));

            var tenants = await sender.Query(new ListTenants(1, 500, null, null), ct);
            tenants.Items.Select(i => i.Id).ShouldNotContain(id => systemTenantIds.Contains(id));

            // search "all" is broad enough to surface the roll-up name were it not filtered.
            var hits = await sender.Query(new Search("all", 50), ct);
            hits.Select(h => h.Id).ShouldNotContain(id => systemOwnerIds.Contains(id) || systemTenantIds.Contains(id));
        }, ct);
    }
}
