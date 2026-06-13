using FluentValidation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
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
/// WP-04: the pg_trgm cross-entity search (§C.5). Fuzzy partial queries rank the right entity first;
/// system rows never surface; another org's rows are never returned; empty q is rejected.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SearchTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Fuzzy_queries_rank_the_right_entity_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        await SeedDirectoryAsync(orgId, ct);

        var hargrove = await SearchAsync(orgId, "hargrov", ct);
        hargrove[0].Type.ShouldBe("owner");
        hargrove[0].Label.ShouldBe("Hargrove Family Trust");

        var oakmont = await SearchAsync(orgId, "oakmont", ct);
        oakmont[0].Type.ShouldBe("property");
        oakmont[0].Label.ShouldBe("412 Oakmont Ave");

        var carter = await SearchAsync(orgId, "carter", ct);
        carter[0].Type.ShouldBe("tenant");
        carter[0].Label.ShouldBe("Jasmine Carter");
    }

    [Fact]
    public async Task System_rows_are_never_returned()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await WithScopeAsync(orgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Set<Owner>().Add(new Owner { Id = UuidV7.NewId(), Name = "All other owners", IsSystem = true });
            await db.SaveChangesAsync(ct);
        }, ct);

        var results = await SearchAsync(orgId, "other owners", ct);
        results.ShouldNotContain(r => r.Label == "All other owners");
    }

    [Fact]
    public async Task Search_is_isolated_across_orgs()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = await NewOrgAsync(ct);
        var orgB = await NewOrgAsync(ct);
        await SeedDirectoryAsync(orgA, ct);

        var bResults = await SearchAsync(orgB, "hargrov", ct);
        bResults.ShouldBeEmpty();
    }

    [Fact]
    public async Task Empty_query_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await Should.ThrowAsync<ValidationException>(() => SearchAsync(orgId, "", ct));
    }

    private async Task SeedDirectoryAsync(Guid orgId, CancellationToken ct)
    {
        await WithScopeAsync(orgId, async sp =>
        {
            var sender = sp.GetRequiredService<ISender>();
            var ownerId = await sender.Send(new CreateOwner("Hargrove Family Trust", "HF", null, null, 800, 0m), ct);
            var propertyId = await sender.Send(new CreateProperty(ownerId, "412 Oakmont Ave", "Asheville", "NC", "28801", null), ct);
            var unitId = await sender.Send(new CreateUnit(propertyId, "#2B", 1450m, "occupied"), ct);
            var tenantId = await sender.Send(new CreateTenant("Jasmine Carter", null, null, "current"), ct);
            await sender.Send(new CreateLease(tenantId, unitId, null, null, 1450m, 1450m, "active"), ct);
        }, ct);
    }

    private async Task<IReadOnlyList<SearchResult>> SearchAsync(Guid orgId, string q, CancellationToken ct)
    {
        IReadOnlyList<SearchResult> result = [];
        await WithScopeAsync(orgId, async sp =>
            result = await sp.GetRequiredService<ISender>().Query(new Search(q, null), ct), ct);
        return result;
    }

    private async Task<Guid> NewOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Search Org {orgId:N}" });
        await migratorDb.SaveChangesAsync(ct);
        return orgId;
    }

    private async Task WithScopeAsync(Guid orgId, Func<IServiceProvider, Task> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        await executor.RunAsync(orgId, () => work(scope.ServiceProvider), ct);
    }
}
