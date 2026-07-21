using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Onboarding;
using LeaseBook.Web.Onboarding.Persistence;
using LeaseBook.Web.Reporting;
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
        bankResult.Counts.Posted.ShouldBe(2);
        bankResult.Counts.AlreadyPosted.ShouldBe(0);
        bankResult.Counts.Errors.ShouldBe(0);

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
        r2Bank.Counts.AlreadyPosted.ShouldBe(r2Bank.RowCount);
        r2Bank.Counts.Posted.ShouldBe(0);

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
    // Fix 2: accrual-basis tie-out to $0 — owner accrual delta offsets receivable
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Owner_accrual_delta_and_matching_receivable_tie_clearing_to_zero_in_accrual_basis()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("AccrualTie", ct);

        // Owner O-1 with one tenant T-1 under it.
        await ImportOwnerTenantChainAsync(setup.Client, ct);

        // A fully-tied cutover set whose ACCRUAL basis nets to $0 specifically because the owner's
        // accrual delta offsets the tenant receivable:
        //   bank (trust) book        500  → clearing CR 500  (Both → both bases)
        //   owner equity cash        500  → clearing DR 500  (Both → both bases)
        //   owner equity accrual Δ   200  → clearing DR 200  (Accrual only)
        //   tenant receivable        200  → clearing CR 200  (Accrual only)
        // Cash net    = +500 (equity) − 500 (bank)                 = 0
        // Accrual net = +500 + 200 (equity+Δ) − 500 − 200 (bank+rcv) = 0  ← the delta/receivable identity
        var bankCsv = $"Account ID,Account Name,Book Balance\n" +
                      $"B-TRUST,{setup.TrustBankName},500.00\n";
        var bankResult = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "bank_balances",
            new { csvContent = bankCsv, cutoverDate = CutoverStr, filename = "banks.csv" }, ct);
        bankResult.ErrorCount.ShouldBe(0, $"bank errors: {string.Join("; ", bankResult.Errors.Select(e => e.Reason))}");

        // owner_balances: cash=500, accrual=700 → accrual delta of 200 (CR owner_equity → DR clearing 200, accrual).
        const string ownerBalCsv =
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\n" +
            "O-1,Chain Owner LLC,500.00,700.00\n";
        var ownerResult = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "owner_balances",
            new { csvContent = ownerBalCsv, cutoverDate = CutoverStr, filename = "owner_balances.csv" }, ct);
        ownerResult.ErrorCount.ShouldBe(0, $"owner errors: {string.Join("; ", ownerResult.Errors.Select(e => e.Reason))}");

        // tenant_receivables: 200 for T-1 (DR tenant_receivable → CR clearing 200, accrual).
        const string receivableCsv =
            "Tenant ID,Owner ID,Balance Due\n" +
            "T-1,O-1,200.00\n";
        var receivableResult = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "tenant_receivables",
            new { csvContent = receivableCsv, cutoverDate = CutoverStr, filename = "receivables.csv" }, ct);
        receivableResult.ErrorCount.ShouldBe(0, $"receivable errors: {string.Join("; ", receivableResult.Errors.Select(e => e.Reason))}");

        var tenant = new TenantContext { OrgId = setup.OrgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        await executor.RunAsync(setup.OrgId, async () =>
        {
            // ACCRUAL identity: the owner accrual-delta clearing DR 200 offsets the receivable clearing
            // CR 200, and the cash legs (equity 500 / bank 500) cancel → accrual ties to exactly $0.00.
            var accrualNet = await ClearingNetAsync(db, "accrual", ct);
            accrualNet.ShouldBe(0m,
                $"accrual clearing must tie to $0 (delta DR 200 offsets receivable CR 200, cash legs cancel); got {accrualNet}");

            // CASH basis sees only the cash legs (equity 500 vs bank 500), which net to $0. The
            // receivable + accrual-delta lines are Basis=Accrual, so they do not touch the cash basis.
            var cashNet = await ClearingNetAsync(db, "cash", ct);
            cashNet.ShouldBe(0m,
                $"cash clearing should net to $0 from the cash legs alone (equity 500 − bank 500); got {cashNet}");
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Fix 1: a $0.00 figure is a no-op (skipped), not a 500
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Zero_bank_and_deposit_figures_are_skipped_no_journal_entry_2xx()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("ZeroFigures", ct);

        await ImportOwnerTenantChainAsync(setup.Client, ct);

        // A $0.00 bank balance row.
        var bankCsv = $"Account ID,Account Name,Book Balance\n" +
                      $"B-TRUST,{setup.TrustBankName},0.00\n";
        var bankResult = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "bank_balances",
            new { csvContent = bankCsv, cutoverDate = CutoverStr, filename = "banks.csv" }, ct);
        bankResult.ErrorCount.ShouldBe(0, "a $0.00 bank row must not error");
        bankResult.RowCount.ShouldBe(1);

        // A $0.00 deposit row.
        const string depositCsv =
            "Tenant ID,Owner ID,Deposit Held\n" +
            "T-1,O-1,0.00\n";
        var depositResult = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "deposit_liabilities",
            new { csvContent = depositCsv, cutoverDate = CutoverStr, filename = "deposits.csv" }, ct);
        depositResult.ErrorCount.ShouldBe(0, "a $0.00 deposit row must not error");

        var tenant = new TenantContext { OrgId = setup.OrgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        await executor.RunAsync(setup.OrgId, async () =>
        {
            // No journal entry was created at all (both rows were no-ops).
            var entryCount = await db.Set<JournalEntry>().CountAsync(ct);
            entryCount.ShouldBe(0, "exactly-zero figures must post no journal entry");

            // Both rows persisted with status 'skipped'.
            var bankRows = await db.Set<ImportRow>()
                .Where(r => r.BatchId == bankResult.BatchId)
                .ToListAsync(ct);
            bankRows.ShouldHaveSingleItem().RowStatus.ShouldBe("skipped");

            var depositRows = await db.Set<ImportRow>()
                .Where(r => r.BatchId == depositResult.BatchId)
                .ToListAsync(ct);
            depositRows.ShouldHaveSingleItem().RowStatus.ShouldBe("skipped");
        }, ct);
    }

    [Fact]
    public async Task Owner_cash_zero_accrual_nonzero_posts_only_the_accrual_delta()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("CashZeroAccrual", ct);

        const string ownersCsv =
            "Owner ID,Owner Name,Reserve\n" +
            "O-1,Cash-Zero Owner,0\n";
        await PostImportAsync<ImportBatchResult>(setup.Client, "owners",
            new { csvContent = ownersCsv, filename = "owners.csv" }, ct);

        // cash=0, accrual=200 → cash line SKIPPED, accrual-delta of 200 STILL posts.
        const string ownerBalCsv =
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\n" +
            "O-1,Cash-Zero Owner,0.00,200.00\n";
        var result = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "owner_balances",
            new { csvContent = ownerBalCsv, cutoverDate = CutoverStr, filename = "owner_balances.csv" }, ct);
        result.ErrorCount.ShouldBe(0);
        result.RowCount.ShouldBe(1);

        var tenant = new TenantContext { OrgId = setup.OrgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        await executor.RunAsync(setup.OrgId, async () =>
        {
            // Exactly ONE entry — the accrual delta. The cash line was a no-op.
            var entryCount = await db.Set<JournalEntry>().CountAsync(ct);
            entryCount.ShouldBe(1, "cash=0 skips the cash line; only the accrual delta posts");

            // Cash clearing unaffected (no cash leg posted).
            var cashNet = await ClearingNetAsync(db, "cash", ct);
            cashNet.ShouldBe(0m, $"cash clearing must be untouched when cash=0; got {cashNet}");

            // Accrual clearing reflects the 200 delta (owner equity CR 200 → clearing DR 200).
            var accrualNet = await ClearingNetAsync(db, "accrual", ct);
            accrualNet.ShouldBe(200m, $"accrual clearing should be the 200 delta; got {accrualNet}");

            // The single CSV row is recorded as posted (the accrual delta posted), not skipped.
            var rows = await db.Set<ImportRow>()
                .Where(r => r.BatchId == result.BatchId)
                .ToListAsync(ct);
            rows.ShouldHaveSingleItem().RowStatus.ShouldBe("posted");
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Fix 3: empty CsvContent → HTTP 400 (empty_csv)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Empty_csv_content_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("EmptyCsv", ct);

        var response = await PostBalanceImportRawAsync(setup.Client, "bank_balances",
            new { csvContent = "", cutoverDate = CutoverStr, filename = "banks.csv" }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var detail = await response.Content.ReadAsStringAsync(ct);
        detail.ShouldContain("empty_csv");

        var problem = System.Text.Json.JsonSerializer.Deserialize<ProblemWithCode>(
            detail, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        problem.Code.ShouldBe("empty_csv");
        problem.CorrelationId.ShouldNotBeNullOrWhiteSpace();
    }

    // -------------------------------------------------------------------------
    // Fix 4: ambiguous bank name (two same-named active banks) → row error
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Ambiguous_bank_name_yields_row_error_not_misroute()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("AmbiguousBank", ct);

        // Create a SECOND active bank with the same name as the trust bank.
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsync(setup.OrgId, async () =>
            {
                await sender.Send(new CreateBankAccount(setup.TrustBankName, null, null, "trust"), ct);
            }, ct);
        }

        var bankCsv = $"Account ID,Account Name,Book Balance\n" +
                      $"B-TRUST,{setup.TrustBankName},500.00\n";
        var result = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "bank_balances",
            new { csvContent = bankCsv, cutoverDate = CutoverStr, filename = "banks.csv" }, ct);

        result.ErrorCount.ShouldBe(1);
        var error = result.Errors.ShouldHaveSingleItem();
        error.Field.ShouldBe("name");
        error.Reason.ShouldContain("ambiguous_bank_name");

        // No journal entry posted for the ambiguous row.
        var tenant = new TenantContext { OrgId = setup.OrgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor2 = new OrgScopedExecutor(db, tenant);
        await executor2.RunAsync(setup.OrgId, async () =>
        {
            (await db.Set<JournalEntry>().CountAsync(ct)).ShouldBe(0);
        }, ct);
    }

    // -------------------------------------------------------------------------
    // WP-7 §0.3 regression: a cutover-month statement over a migrated org must not 500
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Cutover_month_statement_over_a_migrated_org_ties_out()
    {
        // WP-7 §0.3: the per-position import posts "OpeningBalance" at the cutover date, so the
        // statement for the cutover month used to throw UncategorizedEventException (surfacing as
        // a 500). After the fix it folds into Beginning and ties out.
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("CutoverStmt", ct);
        await ImportOwnerTenantChainAsync(setup.Client, ct);

        const string csv = "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,1250.00,1250.00\n";
        await PostBalanceImportAsync<object>(setup.Client, "owner_balances",
            new { csvContent = csv, cutoverDate = CutoverStr, filename = "owners.csv" }, ct);

        // Resolve the imported owner's LeaseBook id the same way every other test in this file
        // resolves directory state after an import: an app-role context bound to this test's org
        // (the TenantContext/OrgScopedExecutor pair used throughout), queried by the name
        // ImportOwnerTenantChainAsync imported O-1 under ("Chain Owner LLC").
        var tenant = new TenantContext { OrgId = setup.OrgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        var ownerId = Guid.Empty;
        await executor.RunAsync(setup.OrgId, async () =>
        {
            ownerId = await db.Set<Owner>().AsNoTracking()
                .Where(o => o.Name == "Chain Owner LLC")
                .Select(o => o.Id)
                .SingleAsync(ct);
        }, ct);

        var response = await setup.Client.GetAsync(
            $"/api/statements/{ownerId}?year=2026&month=6&basis=cash", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(ct));

        var stmt = (await response.Content.ReadFromJsonAsync<StatementView>(ct))!;
        stmt.Beginning.ShouldBe(1250.00m, "the opening position folds into Beginning");
        stmt.Ending.ShouldBe(1250.00m);

        // The tie-out is the point: the independent journal re-query must agree, not just the
        // categorical pipeline's own arithmetic.
        stmt.Fiduciary.Variance.ShouldBe(0m);
        stmt.Fiduciary.Balanced.ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // WP-7 Task 10: held_pm_fees opening-balance import (ADR-020 §5)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Held_fees_import_converts_the_clearing_residual_to_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("HeldFees", ct);
        await ImportOwnerTenantChainAsync(setup.Client, ct);

        // The §5 acceptance shape: the trust bank's book balance also carries $100.00 of unremitted
        // PM fees still sitting inside the account, so bank (600.00) = owner equity (500.00) + held
        // fees (100.00). The deposit side ties independently (deposit bank 500.00 = deposit liability
        // 500.00) and contributes nothing to the gap — it is here only to mirror the standard tied set.
        var bankCsv = $"Account ID,Account Name,Book Balance\n" +
                      $"B-TRUST,{setup.TrustBankName},600.00\n" +
                      $"B-DEP,{setup.DepositBankName},500.00\n";
        var bankResult = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "bank_balances",
            new { csvContent = bankCsv, cutoverDate = CutoverStr, filename = "banks.csv" }, ct);
        bankResult.ErrorCount.ShouldBe(0, $"bank errors: {string.Join("; ", bankResult.Errors.Select(e => e.Reason))}");

        const string ownerBalCsv =
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\n" +
            "O-1,Chain Owner LLC,500.00,500.00\n";
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

        var tenant = new TenantContext { OrgId = setup.OrgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        var trustBankId = Guid.Empty;
        await executor.RunAsync(setup.OrgId, async () =>
        {
            trustBankId = await db.Set<BankAccount>().AsNoTracking()
                .Where(b => b.Name == setup.TrustBankName)
                .Select(b => b.Id)
                .SingleAsync(ct);

            // Before the held-fees import: the trust bank's book (600) exceeds what owner equity (500)
            // accounts for by exactly the $100.00 of held fees not yet recorded — the ADR-021
            // manual-reconciliation residual an operator would otherwise have to true up by hand.
            // Sign: PostOpeningPositionAsync's clearing contra mirrors the opposite side of the real
            // leg (clearingLeg Debit = req.Credit, Credit = req.Debit), so a real DEBIT (the bank
            // position) mirrors to a clearing CREDIT. In ClearingNetAsync's SUM(debit-credit)
            // convention an unmatched bank excess therefore reads NEGATIVE.
            var preNet = await ClearingNetAsync(db, "cash", ct);
            preNet.ShouldBe(-100.00m,
                $"the trust bank's unrecorded $100 of held fees should show as a -100 clearing residual; got {preNet}");
        }, ct);

        // Import the held fees themselves — the row that reconciles the residual.
        var heldCsv = $"Account ID,Account Name,Held Fees\nB-TRUST,{setup.TrustBankName},100.00\n";
        var heldResult = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "held_pm_fees",
            new { csvContent = heldCsv, cutoverDate = CutoverStr, filename = "held_fees.csv" }, ct);
        heldResult.ErrorCount.ShouldBe(0, $"held fees errors: {string.Join("; ", heldResult.Errors.Select(e => e.Reason))}");

        await executor.RunAsync(setup.OrgId, async () =>
        {
            var cashNet = await ClearingNetAsync(db, "cash", ct);
            var accrualNet = await ClearingNetAsync(db, "accrual", ct);
            cashNet.ShouldBe(0m, $"held-fees import should zero out the clearing residual in cash basis; got {cashNet}");
            accrualNet.ShouldBe(0m, $"held-fees import should zero out the clearing residual in accrual basis; got {accrualNet}");

            var heldTerm = await HeldFeesTermAsync(db, trustBankId, ct);
            heldTerm.ShouldBe(100.00m,
                $"the trust equation's held_pm_fees term should read the imported 100.00; got {heldTerm}");
        }, ct);
    }

    [Fact]
    public async Task Held_fees_row_naming_an_operating_bank_is_a_row_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("HeldFeesOperating", ct);

        var heldCsv = $"Account ID,Account Name,Held Fees\nB-OP,{setup.OperatingBankName},100.00\n";
        var result = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "held_pm_fees",
            new { csvContent = heldCsv, cutoverDate = CutoverStr, filename = "held_fees.csv" }, ct);

        result.ErrorCount.ShouldBe(1);
        var error = result.Errors.ShouldHaveSingleItem();
        error.Field.ShouldBe("name");
        error.Reason.ShouldContain("trust bank");
        error.Reason.ShouldNotContain("pm_income");

        // S2: the resolved bank's internal id must not leak into the operator-facing reason, in
        // either Guid.ToString() format ("D" dashed or "N" bare-hex) — the reason echoes only the
        // operator's own CSV text (row.Name), never an internally generated identifier.
        var tenant = new TenantContext { OrgId = setup.OrgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);
        var operatingBankId = Guid.Empty;
        await executor.RunAsync(setup.OrgId, async () =>
        {
            operatingBankId = await db.Set<BankAccount>().AsNoTracking()
                .Where(b => b.Name == setup.OperatingBankName)
                .Select(b => b.Id)
                .SingleAsync(ct);
        }, ct);
        error.Reason.ShouldNotContain(operatingBankId.ToString());
        error.Reason.ShouldNotContain(operatingBankId.ToString("N"));
    }

    [Fact]
    public async Task Held_fees_row_with_unknown_bank_name_is_a_row_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("HeldFeesUnknownBank", ct);

        var heldCsv = "Account ID,Account Name,Held Fees\nB-X,Nonexistent Bank Account,100.00\n";
        var result = await PostBalanceImportAsync<ImportBatchResult>(
            setup.Client, "held_pm_fees",
            new { csvContent = heldCsv, cutoverDate = CutoverStr, filename = "held_fees.csv" }, ct);

        result.ErrorCount.ShouldBe(1);
        var error = result.Errors.ShouldHaveSingleItem();
        error.Field.ShouldBe("name");
        error.Reason.ShouldBe("No bank account named 'Nonexistent Bank Account' found in this org");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed record TestSetup(
        Guid OrgId, HttpClient Client, string TrustBankName, string DepositBankName, string OperatingBankName);

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

        // Create trust + deposit + operating bank accounts for this org via the service layer. The
        // operating bank exists so held-fees row-error tests can name a bank that is NOT trust_bank-class
        // (WP-7 Task 10) without every other test in the file paying for a bank it never uses.
        var trustBankName = $"Operating Trust {orgId:N}";
        var depositBankName = $"Security Deposit Trust {orgId:N}";
        var operatingBankName = $"PM Operating {orgId:N}";

        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsync(orgId, async () =>
            {
                await sender.Send(new CreateBankAccount(trustBankName, null, null, "trust"), ct);
                await sender.Send(new CreateBankAccount(depositBankName, null, null, "deposit"), ct);
                await sender.Send(new CreateBankAccount(operatingBankName, null, null, "operating"), ct);
            }, ct);
        }

        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync(ct));
        await client.PrimeCsrfAsync(ct); // XSRF rotates on sign-in

        return new TestSetup(orgId, client, trustBankName, depositBankName, operatingBankName);
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

    /// <summary>Posts a balance import without asserting 200 — for the 400-path tests.</summary>
    private static Task<HttpResponseMessage> PostBalanceImportRawAsync(
        HttpClient client, string kind, object body, CancellationToken ct) =>
        client.PostAsJsonAsync($"/api/onboarding/import-balances/{kind}", body, ct);

    /// <summary>
    /// Imports one owner (O-1) → property (P-1) → unit (UNIT-1) → tenant/lease (T-1) chain via the
    /// entity-import endpoints, so deposit/receivable balance rows can resolve their owner + tenant ids.
    /// </summary>
    private static async Task ImportOwnerTenantChainAsync(HttpClient client, CancellationToken ct)
    {
        await PostImportAsync<ImportBatchResult>(client, "owners",
            new { csvContent = "Owner ID,Owner Name,Reserve\nO-1,Chain Owner LLC,0\n", filename = "owners.csv" }, ct);
        await PostImportAsync<ImportBatchResult>(client, "properties",
            new { csvContent = "Property ID,Owner ID,Address\nP-1,O-1,1 Chain St\n", filename = "properties.csv" }, ct);
        await PostImportAsync<ImportBatchResult>(client, "units",
            new { csvContent = "Unit ID,Property ID,Unit,Rent,Status\nUNIT-1,P-1,Unit A,1000.00,occupied\n", filename = "units.csv" }, ct);
        await PostImportAsync<ImportBatchResult>(client, "tenants_leases",
            new
            {
                csvContent = "Tenant ID,Unit ID,Tenant Name,Lease Start,Lease End,Rent,Deposit,Status\n" +
                             "T-1,UNIT-1,Chain Tenant,2025-01-01,,1000.00,500.00,active\n",
                filename = "tenants.csv",
            }, ct);
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

    /// <summary>
    /// Queries the trust equation's held_pm_fees term for one bank (mirrors GetTrustEquation's own
    /// pm_income component): SUM(credit - debit) over pm_income lines tagged that bank's id, for cash
    /// + both basis. pm_income is credit-normal, so a positive value is the held-fees balance.
    /// </summary>
    private static async Task<decimal> HeldFeesTermAsync(
        Microsoft.EntityFrameworkCore.DbContext db, Guid bankAccountId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ClearingNet>(
            $"""
            SELECT COALESCE(
                SUM(COALESCE(credit, 0) - COALESCE(debit, 0))
                    FILTER (WHERE basis IN ('cash', 'both')),
                0
            ) AS net
            FROM journal_lines
            WHERE account_class = 'pm_income' AND bank_account_id = {bankAccountId}
            """).ToListAsync(ct);

        return rows.Count == 0 ? 0m : rows[0].Net;
    }

    private sealed record ClearingNet(decimal Net);

    private sealed record ProblemWithCode(string Code, string? CorrelationId);
}
