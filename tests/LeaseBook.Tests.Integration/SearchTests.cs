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

    /// <summary>
    /// #409: a unit result carries the property that owns it, so the palette can open that property
    /// instead of dumping the operator on the properties list. Every other type carries null — the
    /// field is named for units on purpose, so it cannot quietly acquire a second meaning.
    ///
    /// Two properties, each with its own unit, on purpose. Against a single-property fixture the
    /// assertion only proves the id is non-null: `(SELECT id FROM properties LIMIT 1)` or a join that
    /// picked the wrong row would pass it, and #409's whole value is that the id is the *right* one.
    /// </summary>
    [Fact]
    public async Task Unit_results_carry_their_own_owning_property_and_other_types_do_not()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        await SeedDirectoryAsync(orgId, ct);
        var secondPropertyId = Guid.Empty;
        await WithScopeAsync(orgId, async sp =>
        {
            var sender = sp.GetRequiredService<ISender>();
            var ownerId = await sender.Send(new CreateOwner("Pemberton Holdings", "PH", null, null, 800, 0m), ct);
            secondPropertyId = await sender.Send(new CreateProperty(ownerId, "77 Sycamore Way", "Asheville", "NC", "28801", null), ct);
            await sender.Send(new CreateUnit(secondPropertyId, "#9Z", 1200m, "available"), ct);

            // The bank arm is the fifth branch of the union and the one nothing else here reaches.
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(new BankAccount { Id = UuidV7.NewId(), OrgId = orgId, Name = "Sycamore Operating Trust", Purpose = BankPurpose.Trust });
            await db.SaveChangesAsync(ct);
        }, ct);

        var oakmontUnit = (await SearchAsync(orgId, "2B", ct)).First(r => r.Type == "unit");
        var sycamoreUnit = (await SearchAsync(orgId, "9Z", ct)).First(r => r.Type == "unit");
        var oakmont = (await SearchAsync(orgId, "oakmont", ct)).First(r => r.Type == "property");

        oakmontUnit.PropertyId.ShouldNotBeNull();
        oakmontUnit.PropertyId!.Value.ShouldBe(oakmont.Id);
        sycamoreUnit.PropertyId.ShouldNotBeNull();
        sycamoreUnit.PropertyId!.Value.ShouldBe(secondPropertyId);
        // The two units must not share a property id — the failure a one-property fixture cannot see.
        sycamoreUnit.PropertyId!.Value.ShouldNotBe(oakmontUnit.PropertyId!.Value);

        oakmont.PropertyId.ShouldBeNull();
        (await SearchAsync(orgId, "hargrov", ct)).First(r => r.Type == "owner").PropertyId.ShouldBeNull();
        (await SearchAsync(orgId, "carter", ct)).First(r => r.Type == "tenant").PropertyId.ShouldBeNull();
        (await SearchAsync(orgId, "sycamore operating", ct)).First(r => r.Type == "bank").PropertyId.ShouldBeNull();
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
            var unitId = await sender.Send(new CreateUnit(propertyId, "#2B", 1450m, "available"), ct);
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
        await executor.RunAsSystemAsync(orgId, "test-harness", () => work(scope.ServiceProvider), ct);
    }
}
