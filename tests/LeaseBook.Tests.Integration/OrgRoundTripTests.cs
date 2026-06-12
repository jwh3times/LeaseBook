using LeaseBook.SharedKernel;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;

// Testcontainers pulls in BouncyCastle, whose root namespace `Org` shadows the entity type.
using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

[Collection(nameof(DatabaseCollection))]
public sealed class OrgRoundTripTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Org_written_by_migrator_is_readable_by_the_app_role()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();

        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = "Tarheel Property Group" });
            await migratorDb.SaveChangesAsync(ct);
        }

        await using var appDb = fixture.CreateContext(fixture.AppConnectionString);
        var org = await appDb.Orgs.SingleOrDefaultAsync(o => o.Id == orgId, ct);

        org.ShouldNotBeNull();
        org.Name.ShouldBe("Tarheel Property Group");
        org.CreatedAt.ShouldNotBe(default); // stamped by AppDbContext.SaveChanges
    }
}
