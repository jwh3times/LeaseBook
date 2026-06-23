using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Onboarding;
using LeaseBook.Web.Onboarding.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration.Migration;

/// <summary>
/// WP-3 Task 3.1: entity import endpoint end-to-end over HTTP. Uses the same pattern as
/// <c>StatementImportHttpTests</c> (JSON body with csvContent string, WebApplicationFactory auth).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class EntityImportTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";

    // -------------------------------------------------------------------------
    // Happy-path: owners CSV → owners created + posted batch + zero errors
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Import_owners_creates_directory_rows_and_a_posted_batch()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, client) = await SetupAsync("HappyPath", ct);

        const string csv =
            "Owner ID,Owner Name,Reserve\n" +
            "O-1,Hargrove Family Trust,250.00\n" +
            "O-2,Linden Properties LLC,0\n";

        var result = await PostImportAsync<ImportBatchResult>(client, "owners",
            new { csvContent = csv, filename = "owners.csv" }, ct);

        // HTTP 200 with well-formed result.
        result.RowCount.ShouldBe(2);
        result.ErrorCount.ShouldBe(0);
        result.Errors.ShouldBeEmpty();
        result.BatchId.ShouldNotBe(Guid.Empty);

        // Verify that two owners actually exist in the Directory (direct DB assertion, app role + RLS).
        var tenant = new TenantContext();
        tenant.OrgId = orgId;
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        List<Owner> owners = null!;
        await executor.RunAsync(orgId, async () =>
        {
            owners = await db.Set<Owner>()
                .AsNoTracking()
                .OrderBy(o => o.Name)
                .ToListAsync(ct);
        }, ct);

        owners.Count.ShouldBe(2);
        owners.ShouldContain(o => o.Name == "Hargrove Family Trust");
        owners.ShouldContain(o => o.Name == "Linden Properties LLC");

        // Verify the import_batch row is persisted as "posted".
        await executor.RunAsync(orgId, async () =>
        {
            var batch = await db.Set<ImportBatch>()
                .SingleOrDefaultAsync(b => b.Id == result.BatchId, ct);
            batch.ShouldNotBeNull();
            batch!.Status.ShouldBe("posted");
            batch.ErrorCount.ShouldBe(0);
            batch.RowCount.ShouldBe(2);

            var rows = await db.Set<ImportRow>()
                .Where(r => r.BatchId == result.BatchId)
                .ToListAsync(ct);
            rows.Count.ShouldBe(2);
            rows.ShouldAllBe(r => r.RowStatus == "posted");
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Error path: one malformed row → error ImportRow, HTTP 200 not 500
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Import_owners_with_one_bad_row_yields_error_ImportRow_and_http_200()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, client) = await SetupAsync("ErrorPath", ct);

        // Row 2 has an empty name — must be rejected without crashing the batch.
        const string csv =
            "Owner ID,Owner Name,Reserve\n" +
            "O-1,Good Owner LLC,100.00\n" +
            "O-2,,50.00\n";

        var result = await PostImportAsync<ImportBatchResult>(client, "owners",
            new { csvContent = csv, filename = "owners-bad.csv" }, ct);

        // HTTP 200, never 500 for a row-level failure.
        result.ShouldNotBeNull();
        result.ErrorCount.ShouldBe(1);
        result.RowCount.ShouldBe(2);
        result.Errors.ShouldHaveSingleItem().Field.ShouldBe("name");

        // Exactly one owner was created in Directory.
        var tenant = new TenantContext();
        tenant.OrgId = orgId;
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        await executor.RunAsync(orgId, async () =>
        {
            var owners = await db.Set<Owner>().AsNoTracking().ToListAsync(ct);
            owners.Count.ShouldBe(1);
            owners[0].Name.ShouldBe("Good Owner LLC");

            // Verify that the error row is persisted alongside the success row.
            var rows = await db.Set<ImportRow>()
                .Where(r => r.BatchId == result.BatchId)
                .ToListAsync(ct);
            rows.Count.ShouldBe(2);
            rows.ShouldContain(r => r.RowStatus == "posted");
            rows.ShouldContain(r => r.RowStatus == "error");

            // Batch is in "posted_with_errors" status.
            var batch = await db.Set<ImportBatch>()
                .SingleOrDefaultAsync(b => b.Id == result.BatchId, ct);
            batch.ShouldNotBeNull();
            batch!.Status.ShouldBe("posted_with_errors");
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<(Guid orgId, HttpClient client)> SetupAsync(string tag, CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Entity Import Test {tag} {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"import-{orgId:N}@example.com";
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser
            {
                Id = UuidV7.NewId(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                OrgId = orgId,
                DisplayName = "Import Test User",
            };
            (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
        }

        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync(ct));
        await client.PrimeCsrfAsync(ct); // XSRF rotates on sign-in

        return (orgId, client);
    }

    private static async Task<T> PostImportAsync<T>(HttpClient client, string kind, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync($"/api/onboarding/import/{kind}", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }
}
