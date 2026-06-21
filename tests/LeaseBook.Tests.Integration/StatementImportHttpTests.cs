using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Banking.Features.Import;
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
/// WP-05 (M4): the CSV statement import + auto-match flow end-to-end over HTTP. Imports a CSV, sees a line
/// auto-match an uncleared register line, confirms it (which clears the line through the ADR-007 clearance
/// port — proving Banking touches the journal only via Accounting), and proves a re-import is de-duplicated.
/// A second case covers the unmatched → create-transaction-prompt branch. Its own seeded org.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class StatementImportHttpTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";
    private static readonly DateOnly Feb1 = new(2026, 2, 1);

    private static object AmountColumnMap => new { date = "Date", description = "Description", amount = "Amount" };

    [Fact]
    public async Task Import_matches_clears_through_the_port_and_dedups_on_reimport()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        // An uncleared register line (interest = a $100 deposit) for the import to match against.
        await PostOkAsync<PostResult>(client, $"/api/accounting/banks/{setup.TrustBankId}/adjustments",
            new { kind = "interest", amount = 100m, date = Feb1, sourceRef = Key() }, ct);
        var register = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        var registerLineId = register.Rows[0].JournalLineId;

        const string csv = "Date,Description,Amount\n2026-02-01,Interest,100.00\n";

        var import = await PostOkAsync<ImportResult>(client, $"/api/banking/banks/{setup.TrustBankId}/imports",
            new { filename = "statement.csv", csvContent = csv, columnMap = AmountColumnMap }, ct);
        import.Imported.ShouldBe(1);
        import.SkippedDuplicates.ShouldBe(0);
        import.Errors.ShouldBeEmpty();

        // The single line auto-matches the register interest line (exact amount + same date).
        var preview = await GetAsync<MatchPreviewResponse>(client, $"/api/banking/imports/{import.ImportId}/matches", ct);
        var row = preview.Rows.ShouldHaveSingleItem();
        row.Kind.ShouldBe("matched");
        row.JournalLineId.ShouldBe(registerLineId);
        preview.Summary.Matched.ShouldBe(1);

        // Confirming clears the matched register line — through the clearance port, not Banking SQL.
        var confirm = await PostOkAsync<ConfirmMatchesResult>(client, $"/api/banking/imports/{import.ImportId}/confirm",
            new { decisions = new[] { new { statementLineId = row.StatementLineId, journalLineId = row.JournalLineId, kind = "matched" } } }, ct);
        confirm.Cleared.ShouldBe(1);

        var after = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        after.Rows.Single(r => r.JournalLineId == registerLineId).Status.ShouldBe(BankLineStatus.Cleared);

        // Re-importing the identical CSV stores nothing new (dedup), and reports the skip.
        var reimport = await PostOkAsync<ImportResult>(client, $"/api/banking/banks/{setup.TrustBankId}/imports",
            new { filename = "statement.csv", csvContent = csv, columnMap = AmountColumnMap }, ct);
        reimport.Imported.ShouldBe(0);
        reimport.SkippedDuplicates.ShouldBe(1);
    }

    [Fact]
    public async Task An_unmatched_line_becomes_a_create_transaction_prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        // No register line at this amount → unmatched.
        const string csv = "Date,Description,Amount\n2026-02-01,Mystery debit,-42.42\n";
        var import = await PostOkAsync<ImportResult>(client, $"/api/banking/banks/{setup.TrustBankId}/imports",
            new { filename = "statement.csv", csvContent = csv, columnMap = AmountColumnMap }, ct);
        import.Imported.ShouldBe(1);

        var preview = await GetAsync<MatchPreviewResponse>(client, $"/api/banking/imports/{import.ImportId}/matches", ct);
        var row = preview.Rows.ShouldHaveSingleItem();
        row.Kind.ShouldBe("unmatched");
        row.JournalLineId.ShouldBeNull();

        var confirm = await PostOkAsync<ConfirmMatchesResult>(client, $"/api/banking/imports/{import.ImportId}/confirm",
            new { decisions = new[] { new { statementLineId = row.StatementLineId, journalLineId = (Guid?)null, kind = "unmatched" } } }, ct);
        confirm.Cleared.ShouldBe(0);
        confirm.UnmatchedLineIds.ShouldContain(row.StatementLineId);
    }

    private sealed record Setup(Guid OrgId, string Email, Guid TrustBankId);

    private static string Key() => UuidV7.NewId().ToString();

    private async Task<Setup> SetupAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Import HTTP Org {orgId:N}" });
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
