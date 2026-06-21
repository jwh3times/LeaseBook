using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
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
/// WP-03 (M4): the bank register + adjustment + clearance endpoints end-to-end over HTTP against the
/// seeded host. Posts an adjustment, sees it land in the register as uncleared, clears it, and proves a
/// malformed adjustment maps to 400. Runs in its own seeded org so the demo org stays byte-stable.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class BankRegisterHttpTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";
    private static readonly DateOnly Feb1 = new(2026, 2, 1);

    [Fact]
    public async Task Adjustment_appears_in_the_register_and_can_be_cleared()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        // Interest is a deposit-side bank line (bank ↑).
        await PostOkAsync<PostResult>(client, $"/api/accounting/banks/{setup.TrustBankId}/adjustments",
            new { kind = "interest", amount = 100m, date = Feb1, sourceRef = Key() }, ct);

        var register = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        register.Rows.Count.ShouldBe(1);
        var row = register.Rows[0];
        row.Deposit.ShouldBe(100m);
        row.Status.ShouldBe(BankLineStatus.Uncleared);
        register.Totals.Book.ShouldBe(100m);
        register.Totals.UnclearedCount.ShouldBe(1);

        // Clear it through the clearance endpoint.
        var cleared = await PostOkAsync<ClearancesResult>(client, "/api/accounting/banks/clearances",
            new { journalLineIds = new[] { row.JournalLineId } }, ct);
        cleared.Affected.ShouldBe(1);

        var after = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        after.Rows[0].Status.ShouldBe(BankLineStatus.Cleared);
        after.Totals.Cleared.ShouldBe(100m);
        after.Totals.UnclearedCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_transfer_without_a_destination_is_rejected_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        var bad = await client.PostAsJsonAsync($"/api/accounting/banks/{setup.TrustBankId}/adjustments",
            new { kind = "transfer", amount = 50m, date = Feb1, sourceRef = Key() }, ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private sealed record Setup(Guid OrgId, string Email, Guid TrustBankId);

    private static string Key() => UuidV7.NewId().ToString();

    private async Task<Setup> SetupAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Bank HTTP Org {orgId:N}" });
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
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
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
        await client.PrimeCsrfAsync(ct); // XSRF token rotates on sign-in
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
