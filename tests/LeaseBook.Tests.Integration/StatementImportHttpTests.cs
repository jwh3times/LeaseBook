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

    [Fact]
    public async Task A_reupload_brings_back_a_line_with_no_decision_until_one_is_confirmed()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);
        var registerLineId = await PostInterestAsync(client, setup, ct);
        const string csv = "Date,Description,Amount\n2026-02-01,Interest,100.00\n";

        // Leaving the wizard here stores the line and confirms nothing (#671).
        (await ImportAsync(client, setup, csv, ct)).Imported.ShouldBe(1);

        var again = await ImportAsync(client, setup, csv, ct);
        (again.Imported, again.BroughtBack, again.SkippedDuplicates).ShouldBe((0, 1, 0));

        var preview = await PreviewAsync(client, again, ct);
        var row = preview.Rows.ShouldHaveSingleItem();
        (row.Kind, row.JournalLineId).ShouldBe(("matched", registerLineId));

        var confirm = await PostOkAsync<ConfirmMatchesResult>(client, $"/api/banking/imports/{again.ImportId}/confirm",
            new { decisions = new[] { new { statementLineId = row.StatementLineId, journalLineId = row.JournalLineId, kind = "matched" } } }, ct);
        confirm.Cleared.ShouldBe(1);
        var register = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        register.Rows.Single(r => r.JournalLineId == registerLineId).Status.ShouldBe(BankLineStatus.Cleared);

        // Decided now, so the next upload skips it.
        var settled = await ImportAsync(client, setup, csv, ct);
        (settled.Imported, settled.BroughtBack, settled.SkippedDuplicates).ShouldBe((0, 0, 1));
        (await PreviewAsync(client, settled, ct)).Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_corrected_file_previews_the_line_it_brought_back_beside_the_corrected_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);
        var registerLineId = await PostInterestAsync(client, setup, ct);

        var first = await ImportAsync(client, setup,
            "Date,Description,Amount\n2026-02-01,Interest,100.00\n2026-02-02,Fee,\n", ct);
        (first.Imported, first.Errors.Count).ShouldBe((1, 1));

        var corrected = await ImportAsync(client, setup,
            "Date,Description,Amount\n2026-02-01,Interest,100.00\n2026-02-02,Fee,-5.00\n", ct);
        (corrected.Imported, corrected.BroughtBack, corrected.SkippedDuplicates).ShouldBe((1, 1, 0));
        corrected.Errors.ShouldBeEmpty();

        var preview = await PreviewAsync(client, corrected, ct);
        preview.Rows.Select(r => r.Description).ShouldBe(["Interest", "Fee"]);
        preview.Summary.ShouldBe(new MatchPreviewSummary(1, 0, 1));
        preview.Rows.Single(r => r.Description == "Interest").JournalLineId.ShouldBe(registerLineId);

        await PostOkAsync<ConfirmMatchesResult>(client, $"/api/banking/imports/{corrected.ImportId}/confirm",
            new { decisions = preview.Rows.Select(r => new { statementLineId = r.StatementLineId, journalLineId = r.JournalLineId, kind = r.Kind }) }, ct);
        var register = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        register.Rows.Single(r => r.JournalLineId == registerLineId).Status.ShouldBe(BankLineStatus.Cleared);
    }

    [Theory]
    [InlineData("matched")]
    [InlineData("suggested")]
    [InlineData("created")]
    [InlineData("unmatched")]
    public async Task A_line_with_a_recorded_decision_stays_a_skipped_duplicate(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);
        var registerLineId = await PostInterestAsync(client, setup, ct);
        const string csv = "Date,Description,Amount\n2026-02-01,Interest,100.00\n";

        var import = await ImportAsync(client, setup, csv, ct);
        var row = (await PreviewAsync(client, import, ct)).Rows.ShouldHaveSingleItem();
        await PostOkAsync<ConfirmMatchesResult>(client, $"/api/banking/imports/{import.ImportId}/confirm",
            new { decisions = new[] { new { statementLineId = row.StatementLineId, journalLineId = kind == "unmatched" ? null : (Guid?)registerLineId, kind } } }, ct);

        var again = await ImportAsync(client, setup, csv, ct);
        (again.Imported, again.BroughtBack, again.SkippedDuplicates).ShouldBe((0, 0, 1));
        (await PreviewAsync(client, again, ct)).Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_repeating_a_line_with_no_decision_brings_it_back_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);
        const string csv = "Date,Description,Amount\n2026-02-01,Interest,100.00\n2026-02-01,Interest,100.00\n";

        var first = await ImportAsync(client, setup, csv, ct);
        (first.Imported, first.BroughtBack, first.SkippedDuplicates).ShouldBe((1, 0, 1));

        var again = await ImportAsync(client, setup, csv, ct);
        (again.Imported, again.BroughtBack, again.SkippedDuplicates).ShouldBe((0, 1, 1));
        (await PreviewAsync(client, again, ct)).Rows.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_upload_never_brings_back_a_line_from_another_bank_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);
        const string csv = "Date,Description,Amount\n2026-02-01,Interest,100.00\n";
        var otherBankId = await AddBankAccountAsync(setup, ct);
        var otherUrl = $"/api/banking/banks/{otherBankId}/imports";
        var body = new { filename = "statement.csv", csvContent = csv, columnMap = AmountColumnMap };

        // The same line on both accounts: undecided on the first, decided on the other.
        var first = await ImportAsync(client, setup, csv, ct);
        var other = await PostOkAsync<ImportResult>(client, otherUrl, body, ct);
        (other.Imported, other.BroughtBack, other.SkippedDuplicates).ShouldBe((1, 0, 0));
        var otherRow = (await PreviewAsync(client, other, ct)).Rows.ShouldHaveSingleItem();
        await PostOkAsync<ConfirmMatchesResult>(client, $"/api/banking/imports/{other.ImportId}/confirm",
            new { decisions = new[] { new { statementLineId = otherRow.StatementLineId, journalLineId = (Guid?)null, kind = "unmatched" } } }, ct);

        var again = await PostOkAsync<ImportResult>(client, otherUrl, body, ct);
        (again.Imported, again.BroughtBack, again.SkippedDuplicates).ShouldBe((0, 0, 1));
        (await PreviewAsync(client, first, ct)).Rows.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_stale_preview_cannot_clear_a_second_register_line_for_one_statement_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);
        var firstLineId = await PostInterestAsync(client, setup, ct);
        const string csv = "Date,Description,Amount\n2026-02-03,Interest,100.00\n";

        // One tab previews the line against the only candidate.
        var stale = await ImportAsync(client, setup, csv, ct);
        var staleRow = (await PreviewAsync(client, stale, ct)).Rows.ShouldHaveSingleItem();
        staleRow.JournalLineId.ShouldBe(firstLineId);

        // A closer register line appears, and another tab brings the line back against it.
        await PostOkAsync<PostResult>(client, $"/api/accounting/banks/{setup.TrustBankId}/adjustments",
            new { kind = "interest", amount = 100m, date = new DateOnly(2026, 2, 3), sourceRef = Key() }, ct);
        var current = await ImportAsync(client, setup, csv, ct);
        var currentRow = (await PreviewAsync(client, current, ct)).Rows.ShouldHaveSingleItem();
        currentRow.JournalLineId.ShouldNotBe(firstLineId);

        object Decision(MatchPreviewRow row) =>
            new { decisions = new[] { new { statementLineId = row.StatementLineId, journalLineId = row.JournalLineId, kind = "matched" } } };
        var refused = await client.PostAsJsonAsync($"/api/banking/imports/{stale.ImportId}/confirm", Decision(staleRow), ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(ct)).ShouldContain("Import the file again");

        await PostOkAsync<ConfirmMatchesResult>(client, $"/api/banking/imports/{current.ImportId}/confirm", Decision(currentRow), ct);
        (await client.PostAsJsonAsync($"/api/banking/imports/{stale.ImportId}/confirm", Decision(staleRow), ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var register = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        register.Rows.Single(r => r.JournalLineId == firstLineId).Status.ShouldBe(BankLineStatus.Uncleared);
        register.Rows.Single(r => r.JournalLineId == currentRow.JournalLineId).Status.ShouldBe(BankLineStatus.Cleared);
    }

    [Fact]
    public async Task A_statement_line_is_decided_only_once_even_by_confirmations_at_the_same_moment()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);
        var registerLineId = await PostInterestAsync(client, setup, ct);

        var import = await ImportAsync(client, setup, "Date,Description,Amount\n2026-02-01,Interest,100.00\n", ct);
        var row = (await PreviewAsync(client, import, ct)).Rows.ShouldHaveSingleItem();
        var url = $"/api/banking/imports/{import.ImportId}/confirm";
        var body = new { decisions = new[] { new { statementLineId = row.StatementLineId, journalLineId = row.JournalLineId, kind = "matched" } } };

        var responses = await Task.WhenAll(client.PostAsJsonAsync(url, body, ct), client.PostAsJsonAsync(url, body, ct));
        responses.Select(r => r.StatusCode).ShouldBe([HttpStatusCode.OK, HttpStatusCode.BadRequest], ignoreOrder: true);

        (await client.PostAsJsonAsync(url, body, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var register = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        register.Rows.Single(r => r.JournalLineId == registerLineId).Status.ShouldBe(BankLineStatus.Cleared);
    }

    [Fact]
    public async Task A_map_naming_a_column_the_file_lacks_is_refused_and_stores_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);
        var url = $"/api/banking/banks/{setup.TrustBankId}/imports";

        // #645: the file's amount column is "Value", so the mapped "Amount" is absent. It used to import the
        // row at 0.00 and report no errors.
        const string csv = "Date,Description,Value\n2026-02-01,Interest,100.00\n";

        var refused = await client.PostAsJsonAsync(url,
            new { filename = "statement.csv", csvContent = csv, columnMap = AmountColumnMap }, ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await refused.Content.ReadFromJsonAsync<ValidationProblemBody>(ct);
        problem.ShouldNotBeNull();
        problem.Code.ShouldBe("validation_failed");
        problem.CorrelationId.ShouldNotBeNullOrWhiteSpace();
        problem.Errors.ShouldContainKey("ColumnMap.Amount");
        problem.Errors["ColumnMap.Amount"].ShouldHaveSingleItem().ShouldContain("Amount");

        // Nothing was stored: the same row under the right map imports as new, not as a duplicate.
        var import = await PostOkAsync<ImportResult>(client, url, new
        {
            filename = "statement.csv",
            csvContent = csv,
            columnMap = new { date = "Date", description = "Description", amount = "Value" },
        }, ct);
        import.Imported.ShouldBe(1);
        import.SkippedDuplicates.ShouldBe(0);
    }

    private sealed record ValidationProblemBody(
        string? Code, string? CorrelationId, Dictionary<string, string[]> Errors);

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
            await executor.RunAsSystemAsync(orgId, "test-harness", async () =>
                trustBankId = (await sender.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct)).Id, ct);
        }

        return new Setup(orgId, email, trustBankId);
    }

    private async Task<Guid> AddBankAccountAsync(Setup setup, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        Guid bankId = default;
        await executor.RunAsSystemAsync(setup.OrgId, "test-harness", async () =>
            bankId = (await sender.Send(new CreateBankAccount("Second Trust", null, null, "trust"), ct)).Id, ct);
        return bankId;
    }

    /// <summary>An uncleared $100 interest deposit dated Feb 1, for a statement line to match.</summary>
    private static async Task<Guid> PostInterestAsync(HttpClient client, Setup setup, CancellationToken ct)
    {
        await PostOkAsync<PostResult>(client, $"/api/accounting/banks/{setup.TrustBankId}/adjustments",
            new { kind = "interest", amount = 100m, date = Feb1, sourceRef = Key() }, ct);
        var register = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        return register.Rows.ShouldHaveSingleItem().JournalLineId;
    }

    private static Task<ImportResult> ImportAsync(HttpClient client, Setup setup, string csv, CancellationToken ct) =>
        PostOkAsync<ImportResult>(client, $"/api/banking/banks/{setup.TrustBankId}/imports",
            new { filename = "statement.csv", csvContent = csv, columnMap = AmountColumnMap }, ct);

    private static Task<MatchPreviewResponse> PreviewAsync(HttpClient client, ImportResult import, CancellationToken ct) =>
        GetAsync<MatchPreviewResponse>(client, $"/api/banking/imports/{import.ImportId}/matches", ct);

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
