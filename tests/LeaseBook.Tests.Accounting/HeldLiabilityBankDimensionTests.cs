using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// #473 derives a refund's bank from the bucket that holds the liability, so the derivation is sound
/// only if no deposit or prepayment liability line is bank-less. Every posting template sets the bank;
/// this pins it on the seeded fixture orgs, whose journals were built by the seeders and the import
/// path rather than by the composer.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class HeldLiabilityBankDimensionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Every_held_liability_line_in_the_seeded_orgs_names_its_bank()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);

        foreach (var orgId in new[] { DemoSeeder.DemoOrgId, ScenarioSeeder.ScenarioOrgId })
        {
            var (total, bankless) = await CountAsync(orgId, ct);
            total.ShouldBeGreaterThan(0, $"org {orgId} should hold deposit or prepayment liability lines");
            bankless.ShouldBe(0, $"org {orgId} has bank-less liability lines");
        }
    }

    private async Task<(int Total, int Bankless)> CountAsync(Guid orgId, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (int, int) result = default;
        await executor.RunAsSystemAsync(orgId, "test-harness", async () =>
        {
            var lines = db.Set<JournalLine>().AsNoTracking()
                .Where(l => l.AccountClass == AccountClass.DepositLiability);
            result = (await lines.CountAsync(ct), await lines.CountAsync(l => l.BankAccountId == null, ct));
        }, ct);
        return result;
    }
}
