using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
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
/// WP-3 Task 3.2: balance import endpoint end-to-end over HTTP.
///
/// Assertions per test:
///   Tied set      → clearing nets to $0 in both bases (SQL query), rows posted, idempotent re-import.
///   Non-tying set → clearing == quantified gap (import posts successfully, no rejection).
///   Error paths   → unresolvable owner/bank → row error, not 500.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class BalanceImportTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";
    private static readonly DateOnly Cutover = new(2026, 6, 30);
    private const string CutoverStr = "2026-06-30";

    // -------------------------------------------------------------------------
    // Tied import: bank_balance == owner_equity + deposit — clearing nets to $0
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Tied_cutover_set_leaves_clearing_at_zero_in_both_bases()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("Tied", ct);

        // --- entity imports (pre-requisites for balance imports) ---
        const string ownersCsv =
            "Owner ID,Owner Name,Reserve\n" +
            "O-1,Tied Owner LLC,0\n";

        var ownersResult = await PostImportAsync<ImportBatchResult>(
            setup.Client, "owners", new { csvContent = ownersCsv, filename = "owners.csv" }, ct);
        ownersResult.ErrorCount.ShouldBe(0);

        const string tenantsCsv =
            "Tenant ID,Unit ID,Tenant Name,Lease Start,Lease End,Rent,Deposit,Status\n" +
            "T-1," + "UNIT-1" + ",Tied Tenant,2025-01-01,,1000.00,500.00,active\n";

        // We need a unit — import property + unit first
        const string propsCsv =
            "Property ID,Owner ID,Address\n" +
            "P-1,O-1,123 Tied St\n";
        await PostImportAsync<ImportBatchResult>(setup.Client, "properties",
            new { csvContent = propsCsv, filename = "properties.csv" }, ct);

        const string unitsCsv =
            "Unit ID,Property ID,Unit,Rent,Status\n" +
            "UNIT-1,P-1,Unit A,1000.00,occupied\n";
        await PostImportAsync<ImportBatchResult>(setup.Client, "units",
            new { csvContent = unitsCsv, filename = "units.csv" }, ct);

        var tenantsResult = await PostImportAsync<ImportBatchResult>(
            setup.Client, "tenants_leases",
            new { csvContent = tenantsCsv, filename = "tenants.csv" }, ct);
        tenantsResult.ErrorCount.ShouldBe(0);

        // --- balance imports ---
        // bank_balances: trust bank book balance = owner equity + deposit
        // owner equity = 500.00, deposit = 500.00, bank = 1000.00 → tied
        var bankCsv = $"Account ID,Account Name,Book Balance\n" +
                      $"B-TRUST,{setup.TrustBankName},500.00\n" +
                      $"B-DEP,{setup.DepositBankName},500.00\n";

        var bankResult = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "bank_balances",
            new { csvContent = bankCsv, cutoverDate = CutoverStr, filename = "banks.csv" }, ct);
        bankResult.ErrorCount.ShouldBe(0, $"bank errors: {string.Join("; ", bankResult.Errors.Select(e => e.Reason))}");
        bankResult.RowCount.ShouldBe(2);

        const string ownerBalCsv =
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\n" +
            "O-1,Tied Owner LLC,500.00,500.00\n";

        var ownerBalResult = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "owner_balances",
            new { csvContent = ownerBalCsv, cutoverDate = CutoverStr, filename = "owner_balances.csv" }, ct);
        ownerBalResult.ErrorCount.ShouldBe(0, $"owner_bal errors: {string.Join("; ", ownerBalResult.Errors.Select(e => e.Reason))}");

        const string depositCsv =
            "Tenant ID,Owner ID,Deposit Held\n" +
            "T-1,O-1,500.00\n";

        var depositResult = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "deposit_liabilities",
            new { csvContent = depositCsv, cutoverDate = CutoverStr, filename = "deposits.csv" }, ct);
        depositResult.ErrorCount.ShouldBe(0, $"deposit errors: {string.Join("; ", depositResult.Errors.Select(e => e.Reason))}");

        // --- assert clearing nets to $0 in both bases ---
        var tenant = new TenantContext { OrgId = setup.OrgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        await executor.RunAsync(setup.OrgId, async () =>
        {
            var cashNet = await ClearingNetAsync(db, "cash", ct);
            var accrualNet = await ClearingNetAsync(db, "accrual", ct);

            cashNet.ShouldBe(0m,
                $"migration_clearing cash net should be $0 after tied import; got {cashNet}");
            accrualNet.ShouldBe(0m,
                $"migration_clearing accrual net should be $0 after tied import; got {accrualNet}");
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Idempotent re-import: re-posting the same rows yields "already-posted" rows,
    // no second journal entries, and the clearing stays at $0
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Re_import_is_idempotent_rows_already_posted_no_second_journal_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("Idempotent", ct);

        // Import one owner
        const string ownersCsv =
            "Owner ID,Owner Name,Reserve\n" +
            "O-1,Idempotent Owner LLC,0\n";
        await PostImportAsync<ImportBatchResult>(setup.Client, "owners",
            new { csvContent = ownersCsv, filename = "owners.csv" }, ct);

        // Build bank csv with the trust bank name
        var bankCsvStr = $"Account ID,Account Name,Book Balance\n" +
                         $"B-TRUST,{setup.TrustBankName},200.00\n";

        var ownerBalCsv =
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\n" +
            "O-1,Idempotent Owner LLC,200.00,200.00\n";

        // First import
        var r1Bank = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "bank_balances",
            new { csvContent = bankCsvStr, cutoverDate = CutoverStr, filename = "banks.csv" }, ct);
        r1Bank.ErrorCount.ShouldBe(0);

        var r1Owner = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "owner_balances",
            new { csvContent = ownerBalCsv, cutoverDate = CutoverStr, filename = "owner_balances.csv" }, ct);
        r1Owner.ErrorCount.ShouldBe(0);

        // Count journal entries after first import
        var tenant = new TenantContext { OrgId = setup.OrgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        int firstCount = 0;
        await executor.RunAsync(setup.OrgId, async () =>
        {
            firstCount = await db.Set<JournalEntry>().CountAsync(ct);
        }, ct);

        firstCount.ShouldBeGreaterThan(0);

        // Re-import the same data
        var r2Bank = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "bank_balances",
            new { csvContent = bankCsvStr, cutoverDate = CutoverStr, filename = "banks.csv" }, ct);
        r2Bank.ErrorCount.ShouldBe(0);
        // Re-import rows should be already-posted, not errors
        r2Bank.RowCount.ShouldBe(1);

        var r2Owner = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "owner_balances",
            new { csvContent = ownerBalCsv, cutoverDate = CutoverStr, filename = "owner_balances.csv" }, ct);
        r2Owner.ErrorCount.ShouldBe(0);

        // Journal entry count must not increase
        int secondCount = 0;
        await executor.RunAsync(setup.OrgId, async () =>
        {
            secondCount = await db.Set<JournalEntry>().CountAsync(ct);
        }, ct);

        secondCount.ShouldBe(firstCount, "re-import must not create additional journal entries");

        // Verify already-posted rows in the second batch
        await executor.RunAsync(setup.OrgId, async () =>
        {
            var secondBatch = await db.Set<ImportBatch>()
                .Where(b => b.EntityKind == "BankBalances" && b.Status == "posted")
                .OrderByDescending(b => b.CreatedAt)
                .FirstOrDefaultAsync(ct);
            secondBatch.ShouldNotBeNull();

            var secondBatchRows = await db.Set<ImportRow>()
                .Where(r => r.BatchId == secondBatch!.Id)
                .ToListAsync(ct);
            secondBatchRows.ShouldAllBe(r => r.RowStatus == "already-posted");
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Non-tying import: import posts successfully and clearing == gap (not rejected)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Non_tying_import_posts_successfully_and_clearing_equals_gap()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("NonTying", ct);

        // Import one owner
        const string ownersCsv =
            "Owner ID,Owner Name,Reserve\n" +
            "O-1,Non-Tying Owner,0\n";
        await PostImportAsync<ImportBatchResult>(setup.Client, "owners",
            new { csvContent = ownersCsv, filename = "owners.csv" }, ct);

        // Post only the bank balance (1000.00) without an owner equity to match.
        // The clearing account will have a net debit of 1000.00 (the gap).
        var bankCsvStr = $"Account ID,Account Name,Book Balance\n" +
                         $"B-TRUST,{setup.TrustBankName},1000.00\n";

        var bankResult = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "bank_balances",
            new { csvContent = bankCsvStr, cutoverDate = CutoverStr, filename = "banks.csv" }, ct);

        // Must return 2xx and post — non-tying import is NOT rejected
        bankResult.ErrorCount.ShouldBe(0, "non-tying import must post without error");
        bankResult.RowCount.ShouldBe(1);

        // Clearing should show the gap (1000.00), not $0
        var tenant = new TenantContext { OrgId = setup.OrgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        await executor.RunAsync(setup.OrgId, async () =>
        {
            var cashNet = await ClearingNetAsync(db, "cash", ct);
            cashNet.ShouldBe(-1000m, $"clearing cash net should equal the gap (bank debit = clearing credit); got {cashNet}");
            // The bank debit → clearing CREDIT → clearing net = -1000 in debit-normal clearing terms
            // (debit = positive, credit = negative in our net formula SUM(debit - credit))
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Error path: unresolvable owner → row error, HTTP 200 not 500
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Unresolvable_owner_yields_row_error_not_500()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("UnresolvableOwner", ct);

        // No owners imported — resolution will fail for every row
        const string ownerBalCsv =
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\n" +
            "O-MISSING,Ghost Owner,1000.00,1000.00\n";

        var result = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "owner_balances",
            new { csvContent = ownerBalCsv, cutoverDate = CutoverStr, filename = "owner_balances.csv" }, ct);

        result.ErrorCount.ShouldBe(1);
        result.RowCount.ShouldBe(1);
        result.Errors.ShouldHaveSingleItem().Field.ShouldBe("external_owner_id");
    }

    // -------------------------------------------------------------------------
    // Error path: bank name not found → row error, HTTP 200 not 500
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Unresolvable_bank_name_yields_row_error_not_500()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("UnresolvableBank", ct);

        var bankCsvStr = "Account ID,Account Name,Book Balance\n" +
                         "B-X,Nonexistent Bank Account,500.00\n";

        var result = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "bank_balances",
            new { csvContent = bankCsvStr, cutoverDate = CutoverStr, filename = "banks.csv" }, ct);

        result.ErrorCount.ShouldBe(1);
        result.Errors.ShouldHaveSingleItem().Field.ShouldBe("name");
    }

    // -------------------------------------------------------------------------
    // Accrual delta: owner with accrual ≠ cash posts two entries
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Owner_with_accrual_delta_posts_two_entries_and_clearing_net_includes_delta()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("AccrualDelta", ct);

        const string ownersCsv =
            "Owner ID,Owner Name,Reserve\n" +
            "O-1,Accrual Owner,0\n";
        await PostImportAsync<ImportBatchResult>(setup.Client, "owners",
            new { csvContent = ownersCsv, filename = "owners.csv" }, ct);

        // cash=500, accrual=700 → delta=200, two journal entries should be posted
        const string ownerBalCsv =
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\n" +
            "O-1,Accrual Owner,500.00,700.00\n";

        var result = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "owner_balances",
            new { csvContent = ownerBalCsv, cutoverDate = CutoverStr, filename = "owner_balances.csv" }, ct);

        result.ErrorCount.ShouldBe(0);
        result.RowCount.ShouldBe(1); // one CSV row

        var tenant = new TenantContext { OrgId = setup.OrgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        await executor.RunAsync(setup.OrgId, async () =>
        {
            // Two journal entries created: one for cash (Both) and one for accrual delta (Accrual).
            var entryCount = await db.Set<JournalEntry>().CountAsync(ct);
            entryCount.ShouldBe(2, "cash line + accrual-delta line = two journal entries");

            // Cash basis net: DR clearing 500 (from owner equity CR) = net debit 500
            var cashNet = await ClearingNetAsync(db, "cash", ct);
            cashNet.ShouldBe(500m, $"cash clearing net should reflect the 500 clearing debit from owner equity CR 500; got {cashNet}");

            // Accrual basis net: DR clearing 500 (cash, basis=both) + DR clearing 200 (delta, basis=accrual) = 700
            var accrualNet = await ClearingNetAsync(db, "accrual", ct);
            accrualNet.ShouldBe(700m, $"accrual clearing net should be 700 (500+200 delta); got {accrualNet}");
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed record TestSetup(Guid OrgId, HttpClient Client, string TrustBankName, string DepositBankName);

    private async Task<TestSetup> SetupAsync(string tag, CancellationToken ct)
    {
        var orgId = UuidV7.NewId();

        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Balance Import Test {tag} {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"bal-import-{orgId:N}@example.com";
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
                DisplayName = "Balance Import Test User",
            };
            (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
        }

        // Create trust + deposit bank accounts for this org via the service layer.
        var trustBankName = $"Operating Trust {orgId:N}";
        var depositBankName = $"Security Deposit Trust {orgId:N}";

        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsync(orgId, async () =>
            {
                await sender.Send(new CreateBankAccount(trustBankName, null, null, "trust"), ct);
                await sender.Send(new CreateBankAccount(depositBankName, null, null, "deposit"), ct);
            }, ct);
        }

        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync(ct));
        await client.PrimeCsrfAsync(ct); // XSRF rotates on sign-in

        return new TestSetup(orgId, client, trustBankName, depositBankName);
    }

    private static async Task<T> PostImportAsync<T>(
        HttpClient client, string kind, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync($"/api/onboarding/import/{kind}", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    private static async Task<T> PostBalanceImportAsync<T>(
        HttpClient client, string kind, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync($"/api/onboarding/import-balances/{kind}", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    /// <summary>
    /// Queries the migration_clearing net for the given basis ('cash' or 'accrual').
    /// Returns SUM(debit - credit) FILTER (WHERE basis IN (basis, 'both')).
    /// A positive value means net debit; negative means net credit.
    /// Zero means the clearing account is balanced.
    /// </summary>
    private static async Task<decimal> ClearingNetAsync(
        Microsoft.EntityFrameworkCore.DbContext db, string basis, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ClearingNet>(
            $"""
            SELECT COALESCE(
                SUM(COALESCE(debit, 0) - COALESCE(credit, 0))
                    FILTER (WHERE basis IN ({basis}, 'both')),
                0
            ) AS net
            FROM journal_lines
            WHERE account_class = 'migration_clearing'
            """).ToListAsync(ct);

        return rows.Count == 0 ? 0m : rows[0].Net;
    }

    private sealed record ClearingNet(decimal Net);
}
