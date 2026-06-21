using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-04 (M4): the reconciliation flow end-to-end over HTTP — start → finalize (rejected at non-zero,
/// 200 at zero) → the locked month rejects new bank postings (409 <c>account_period_locked</c>) → admin
/// unlock reopens it. Plus the stored report and history reads. Its own seeded org.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ReconciliationHttpTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";
    private static readonly DateOnly Feb1 = new(2026, 2, 1);
    private static readonly DateOnly Feb5 = new(2026, 2, 5);
    private static readonly DateOnly Feb6 = new(2026, 2, 6);

    [Fact]
    public async Task Reconcile_finalize_lock_and_unlock_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        // A bank line in February (interest = a deposit).
        await PostOkAsync<PostResult>(client, $"/api/accounting/banks/{setup.TrustBankId}/adjustments",
            new { kind = "interest", amount = 100m, date = Feb1, sourceRef = Key() }, ct);
        var register = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        var lineId = register.Rows[0].JournalLineId;

        // Start reconciliation: statement 100, nothing cleared yet → difference 100.
        var started = await PostOkAsync<ReconciliationView>(client, "/api/accounting/reconciliations",
            new { bankAccountId = setup.TrustBankId, year = 2026, month = 2, statementEndingBalance = 100m }, ct);
        started.Difference.ShouldBe(100m);

        // Finalize now → 409 reconciliation_unbalanced.
        var unbalanced = await client.PostAsJsonAsync($"/api/accounting/reconciliations/{started.Id}/finalize", new { }, ct);
        unbalanced.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await unbalanced.Content.ReadFromJsonAsync<ProblemWithCode>(ct))!.Code.ShouldBe("reconciliation_unbalanced");

        // Clear the line → finalize → 200, difference 0, status finalized.
        await PostOkAsync<ClearancesResult>(client, "/api/accounting/banks/clearances",
            new { journalLineIds = new[] { lineId } }, ct);
        var finalized = await PostOkAsync<ReconciliationView>(client,
            $"/api/accounting/reconciliations/{started.Id}/finalize", new { }, ct);
        finalized.Status.ShouldBe("finalized");
        finalized.Difference.ShouldBe(0m);

        // A new bank posting into the locked February → 409 account_period_locked.
        var locked = await client.PostAsJsonAsync($"/api/accounting/banks/{setup.TrustBankId}/adjustments",
            new { kind = "interest", amount = 5m, date = Feb5, sourceRef = Key() }, ct);
        locked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await locked.Content.ReadFromJsonAsync<ProblemWithCode>(ct))!.Code.ShouldBe("account_period_locked");

        // The stored report and the history are readable.
        var report = await GetAsync<ReconciliationReportResponse>(client, $"/api/accounting/reconciliations/{started.Id}/report", ct);
        report.ReportJson.ShouldNotBeNull();
        var history = await GetAsync<ReconciliationHistoryResponse>(client,
            $"/api/accounting/reconciliations?bankAccountId={setup.TrustBankId}", ct);
        history.Rows.ShouldContain(r => r.Id == started.Id && r.Status == "finalized" && r.HasReport);

        // Admin unlock reopens it → the February posting now succeeds.
        await PostOkAsync<ReconciliationView>(client, $"/api/accounting/reconciliations/{started.Id}/unlock",
            new { reason = "correcting a miskeyed statement balance" }, ct);
        await PostOkAsync<PostResult>(client, $"/api/accounting/banks/{setup.TrustBankId}/adjustments",
            new { kind = "interest", amount = 5m, date = Feb6, sourceRef = Key() }, ct);
    }

    private sealed record ProblemWithCode(string Code);

    private sealed record Setup(Guid OrgId, string Email, Guid TrustBankId);

    private static string Key() => UuidV7.NewId().ToString();

    private async Task<Setup> SetupAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Recon HTTP Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"renee-{orgId:N}@example.com";
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
            // Broker-in-charge: both staff (post/reconcile) and admin (unlock).
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
            (await userManager.AddToRoleAsync(user, Roles.PMAdmin)).Succeeded.ShouldBeTrue();
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

    private async Task<HttpClient> LoggedInClientAsync(Setup setup, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(setup.Email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.PrimeCsrfAsync(ct);
        return client;
    }

    private static async Task<T> PostOkAsync<T>(HttpClient client, string url, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(url, body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url, CancellationToken ct)
    {
        var response = await client.GetAsync(url, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }
}
