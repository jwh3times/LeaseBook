using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-8: the compliance-pack endpoint. PMAdmin-only (it carries the audit-log extract), returns a ZIP,
/// and records a compliance-pack-generated audit event on each generation. Each test owns its org.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CompliancePackEndpointTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";

    [Fact]
    public async Task PMAdmin_downloads_a_zip_for_a_closed_period_and_the_generation_is_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(admin: true, ct);
        var client = await LoggedInClientAsync(setup.Email, ct);

        // Close December 2026 (a zero-activity bank reconciles trivially, difference 0.00) and pull a
        // pack scoped to that single closed month.
        await FinalizeZeroReconciliationAsync(client, setup.TrustBankId, 2026, 12, ct);

        var url = $"/api/reports/compliance-pack?bankAccountId={setup.TrustBankId}&from=2026-12-01&to=2026-12-31";
        var response = await client.GetAsync(url, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        bytes.Length.ShouldBeGreaterThan(0);
        bytes[0].ShouldBe((byte)'P'); // ZIP local-file-header magic "PK"
        bytes[1].ShouldBe((byte)'K');

        (await CountPackAuditsAsync(setup.OrgId, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_period_with_any_open_month_is_rejected_with_422()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(admin: true, ct);
        var client = await LoggedInClientAsync(setup.Email, ct);

        // Lock only the END month (December); January–November stay open. The gate requires EVERY month
        // in the period to be closed, so an end-month-only lock must still be rejected.
        await FinalizeZeroReconciliationAsync(client, setup.TrustBankId, 2026, 12, ct);

        var url = $"/api/reports/compliance-pack?bankAccountId={setup.TrustBankId}&from=2026-01-01&to=2026-12-31";
        var response = await client.GetAsync(url, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<ProblemWithTitle>(ct))!.Title.ShouldBe("period_not_closed");
    }

    [Fact]
    public async Task PMStaff_without_admin_is_forbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(admin: false, ct);
        var client = await LoggedInClientAsync(setup.Email, ct);

        var url = $"/api/reports/compliance-pack?bankAccountId={setup.TrustBankId}&from=2026-01-01&to=2026-12-31";
        var response = await client.GetAsync(url, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private sealed record Setup(Guid OrgId, string Email, Guid TrustBankId);

    private sealed record ProblemWithTitle(string Title);

    private sealed record ReconStarted(Guid Id, decimal Difference);

    private static async Task FinalizeZeroReconciliationAsync(
        HttpClient client, Guid bankId, int year, int month, CancellationToken ct)
    {
        var started = await client.PostAsJsonAsync("/api/accounting/reconciliations",
            new { bankAccountId = bankId, year, month, statementEndingBalance = 0m }, ct);
        started.StatusCode.ShouldBe(HttpStatusCode.OK, await started.Content.ReadAsStringAsync(ct));
        var body = (await started.Content.ReadFromJsonAsync<ReconStarted>(ct))!;
        body.Difference.ShouldBe(0m);

        var finalize = await client.PostAsJsonAsync(
            $"/api/accounting/reconciliations/{body.Id}/finalize", new { }, ct);
        finalize.StatusCode.ShouldBe(HttpStatusCode.OK, await finalize.Content.ReadAsStringAsync(ct));
    }

    private async Task<Setup> SetupAsync(bool admin, CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Pack Endpoint Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"user-{orgId:N}@example.com";
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
                DisplayName = "Renée Calloway",
            };
            (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
            if (admin)
            {
                (await userManager.AddToRoleAsync(user, Roles.PMAdmin)).Succeeded.ShouldBeTrue();
            }
        }

        Guid trustBankId = default;
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsync(orgId, async () =>
                trustBankId = (await sender.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct)).Id, ct);
        }

        return new Setup(orgId, email, trustBankId);
    }

    private async Task<int> CountPackAuditsAsync(Guid orgId, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = 0;
        await executor.RunAsync(orgId, async () =>
            count = await db.AuditEvents.CountAsync(a => a.EntityType == "compliance-pack-generated", ct), ct);
        return count;
    }

    private async Task<HttpClient> LoggedInClientAsync(string email, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.PrimeCsrfAsync(ct);
        return client;
    }
}
