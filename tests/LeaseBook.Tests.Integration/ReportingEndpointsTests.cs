using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Reporting.Catalog;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Reporting;
using LeaseBook.Web.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-03 (M5): reporting endpoints — catalog, statement, and preview — over HTTP against the seeded host.
/// <list type="bullet">
/// <item><c>GET /api/reports</c> returns all 8 catalog entries with correct categories.</item>
/// <item><c>GET /api/statements/{ownerId}</c> returns the O5 May 2026 cash statement with the golden ending 22,640.30.</item>
/// <item><c>GET /api/reports/owner-bal/preview</c> returns rows with decimal amounts.</item>
/// <item><c>GET /api/reports/rent-roll/preview</c> returns 20 rows (seed: 20 non-system units).</item>
/// <item>Unauthenticated requests get 401.</item>
/// </list>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ReportingEndpointsTests(PostgresFixture fixture)
{
    // ─── catalog ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Catalog_returns_all_reports_in_prototype_order()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var catalog = await GetAsync<IReadOnlyList<ReportDescriptor>>(client, "/api/reports", ct);

        // The 8 prototype reports, in order, followed by the WP-8 Compliance pack.
        catalog.Count.ShouldBe(9);
        catalog.Select(r => r.Id).ShouldBe(
        [
            "owner-stmt", "owner-bal", "trust-ledger", "bank-rec",
            "deposit-liab", "rent-roll", "delinquency", "mgmt-fee",
            "compliance-pack",
        ], ignoreOrder: false);
    }

    [Fact]
    public async Task Catalog_has_correct_category_assignments()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var catalog = await GetAsync<IReadOnlyList<ReportDescriptor>>(client, "/api/reports", ct);

        catalog.Where(r => r.Category == "Owner").Select(r => r.Id)
            .ShouldBe(["owner-stmt", "owner-bal", "rent-roll"], ignoreOrder: true);
        catalog.Where(r => r.Category == "Trust accounting").Select(r => r.Id)
            .ShouldBe(["trust-ledger", "deposit-liab", "mgmt-fee"], ignoreOrder: true);
        catalog.Where(r => r.Category == "Banking").Select(r => r.Id)
            .ShouldBe(["bank-rec", "delinquency"], ignoreOrder: true);
    }

    [Fact]
    public async Task Catalog_favorites_match_prototype()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var catalog = await GetAsync<IReadOnlyList<ReportDescriptor>>(client, "/api/reports", ct);

        catalog.Where(r => r.Favorite).Select(r => r.Id)
            .ShouldBe(["owner-stmt", "owner-bal", "bank-rec"], ignoreOrder: true);
    }

    // ─── statement ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Statement_O5_May2026_cash_ending_matches_golden_22640_30()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var url = $"/api/statements/{DemoIds.O5}?year=2026&month=5&basis=cash";
        var stmt = await GetAsync<StatementView>(client, url, ct);

        stmt.OwnerId.ShouldBe(DemoIds.O5);
        stmt.Basis.ShouldBe("cash");
        stmt.Year.ShouldBe(2026);
        stmt.Month.ShouldBe(5);
        stmt.Beginning.ShouldBe(21_345.30m);
        stmt.Ending.ShouldBe(22_640.30m);

        // Fiduciary panel: must be balanced (the Accounting tie-out structural guarantee).
        stmt.Fiduciary.Balanced.ShouldBeTrue();
        stmt.Fiduciary.Variance.ShouldBe(0m);
        stmt.Fiduciary.PmIncomeExcluded.ShouldBeTrue();

        // WP-6 (M6): ReconciliationSnapshotRow now includes bankName + accountMask.
        // The demo seed finalizes the Operating Trust for April 2026, so the statement must
        // surface that snapshot with the bank's display name ("Operating Trust") populated.
        stmt.Fiduciary.LatestReconciledBank.ShouldNotBeNull(
            "the seeded April 2026 finalized reconciliation must appear on the statement");
        stmt.Fiduciary.LatestReconciledBank!.Year.ShouldBe(2026);
        stmt.Fiduciary.LatestReconciledBank.Month.ShouldBe(4);
        stmt.Fiduciary.LatestReconciledBank.BankName.ShouldBe("Operating Trust",
            "bankName must be populated from the Directory bank-account record");
        // The seeded OperBank has mask "4021" (set in DemoDirectorySeed).
        stmt.Fiduciary.LatestReconciledBank.AccountMask.ShouldBe("4021",
            "accountMask must be populated from the Directory bank-account record");

        // Owner display name should be resolved.
        stmt.OwnerName.ShouldBe("Ridgeline Investments");
    }

    [Fact]
    public async Task Statement_for_period_with_no_activity_returns_zero_figures()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        // Year 2000 has no journal activity for O5; the assembler returns a zeroed statement
        // (beginning = 0, no sections, ending = 0) rather than 404 — the owner is known to the org.
        var url = $"/api/statements/{DemoIds.O5}?year=2000&month=1&basis=cash";
        var stmt = await GetAsync<StatementView>(client, url, ct);
        stmt.Beginning.ShouldBe(0m);
        stmt.Ending.ShouldBe(0m);
        stmt.Sections.ShouldBeEmpty();
    }

    // ─── preview ───────────────────────────────────────────────────────────────
    // All preview tests now deserialize to PreviewSpaResponse — the { columns, rows, totalRows, message }
    // shape the SPA's useReportPreview hook and ReportPreviewTable expect.

    [Fact]
    public async Task Preview_owner_bal_returns_rows_with_decimal_amounts()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var result = await GetAsync<PreviewSpaResponse>(client, "/api/reports/owner-bal/preview", ct);

        result.Rows.ShouldNotBeEmpty();
        result.TotalRows.ShouldBe(result.Rows.Count);
        result.Columns.ShouldNotBeEmpty("columns must be populated from the first row's keys");
    }

    [Fact]
    public async Task Preview_rent_roll_returns_20_non_system_units()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var result = await GetAsync<PreviewSpaResponse>(client, "/api/reports/rent-roll/preview", ct);

        // 20 non-system units seeded (data.jsx: p1=4, p2=1, p3=6, p4=3, p5=4, p6=2).
        result.TotalRows.ShouldBe(20);
        result.Rows.Count.ShouldBe(20);
        result.Columns.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Preview_deposit_liab_returns_non_empty_rows_with_t1_deposit()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var result = await GetAsync<PreviewSpaResponse>(client, "/api/reports/deposit-liab/preview", ct);

        result.Rows.ShouldNotBeEmpty();
        result.Message.ShouldBeNull();
        result.Columns.ShouldContain("tenantId");
        result.Columns.ShouldContain("held");

        // T1 (Jasmine Carter) has a 1,450.00 security deposit from the balance forward seed.
        // Rows are dictionaries — deserialize to check the T1 entry.
        var rows = result.Rows
            .OfType<System.Text.Json.JsonElement>()
            .ToList();

        var t1Row = rows.FirstOrDefault(r =>
            r.TryGetProperty("tenantId", out var tid) &&
            tid.GetGuid() == DemoIds.T1);
        t1Row.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object,
            "T1 should have a held deposit row");
        t1Row.GetProperty("held").GetDecimal().ShouldBe(1_450.00m);
        t1Row.GetProperty("kind").GetString().ShouldBe("deposit");
    }

    [Fact]
    public async Task Preview_trust_ledger_returns_non_empty_rows_for_operating_trust()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var result = await GetAsync<PreviewSpaResponse>(client, "/api/reports/trust-ledger/preview", ct);

        result.Rows.ShouldNotBeEmpty("Operating Trust has journal activity from the seed");
        result.Message.ShouldBeNull();
        result.Columns.ShouldContain("journalLineId");
        result.Columns.ShouldContain("date");
        result.Columns.ShouldContain("status");

        // Every row must have the expected shape keys.
        var rows = result.Rows.OfType<System.Text.Json.JsonElement>().ToList();
        foreach (var row in rows)
        {
            row.TryGetProperty("journalLineId", out _).ShouldBeTrue("row missing journalLineId");
            row.TryGetProperty("date", out _).ShouldBeTrue("row missing date");
            row.TryGetProperty("status", out _).ShouldBeTrue("row missing status");
        }
    }

    // ─── basis (#230) ──────────────────────────────────────────────────────────
    // owner-bal is the only preview whose figures move with basis: owner_equity is credited
    // 'accrual' on RentCharged and 'cash' on PaymentReceived, so the two bases answer different
    // questions. Everything else a preview reads (trust_bank, pm_operating_bank, deposit_liability,
    // pm_income) is posted 'both', so a basis there would reload and return the same numbers —
    // the dead-toggle defect #229 removed. These assertions are structural rather than
    // seed-dependent: they hold for any org's data.

    [Fact]
    public async Task Preview_owner_bal_echoes_the_basis_it_applied()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var byDefault = await GetAsync<PreviewSpaResponse>(client, "/api/reports/owner-bal/preview", ct);
        var accrual = await GetAsync<PreviewSpaResponse>(
            client, "/api/reports/owner-bal/preview?basis=accrual", ct);
        var nonsense = await GetAsync<PreviewSpaResponse>(
            client, "/api/reports/owner-bal/preview?basis=sideways", ct);

        // The echo is what the SPA labels the figures with, so it must be the resolved basis rather
        // than whatever the client sent.
        byDefault.Basis.ShouldBe("cash", "cash is the default basis");
        accrual.Basis.ShouldBe("accrual");
        nonsense.Basis.ShouldBe("cash", "an unrecognised basis resolves to cash, never to an error");
    }

    [Fact]
    public async Task Preview_owner_bal_deposits_are_identical_on_both_bases()
    {
        // Deposits are liabilities until applied, identically in both bases: every deposit_liability
        // line is posted 'both'. Only the Operating column may move.
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var cash = await GetAsync<PreviewSpaResponse>(client, "/api/reports/owner-bal/preview?basis=cash", ct);
        var accrual = await GetAsync<PreviewSpaResponse>(
            client, "/api/reports/owner-bal/preview?basis=accrual", ct);

        static Dictionary<Guid, decimal> DepositsByOwner(PreviewSpaResponse r) =>
            r.Rows.OfType<System.Text.Json.JsonElement>()
                .ToDictionary(
                    row => row.GetProperty("ownerId").GetGuid(),
                    row => row.GetProperty("deposits").GetDecimal());

        var cashDeposits = DepositsByOwner(cash);
        cashDeposits.ShouldNotBeEmpty("the demo org has owners with attributed deposits");
        foreach (var (ownerId, deposits) in DepositsByOwner(accrual))
        {
            cashDeposits.ShouldContainKey(ownerId);
            cashDeposits[ownerId].ShouldBe(deposits,
                $"deposits for owner {ownerId} must not move with basis");
        }
    }

    [Fact]
    public async Task Preview_trust_ledger_has_no_basis_dimension()
    {
        // The guard against re-adding a decorative control: a bank register is a cash record by
        // construction, so asking for accrual must change nothing and must not claim a basis.
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var plain = await GetAsync<PreviewSpaResponse>(client, "/api/reports/trust-ledger/preview", ct);
        var accrual = await GetAsync<PreviewSpaResponse>(
            client, "/api/reports/trust-ledger/preview?basis=accrual", ct);

        plain.Basis.ShouldBeNull("a report with no basis dimension must echo null, not a basis");
        accrual.Basis.ShouldBeNull("asking for a basis does not give the report one");

        static List<Guid> LineIds(PreviewSpaResponse r) =>
            [.. r.Rows.OfType<System.Text.Json.JsonElement>()
                .Select(row => row.GetProperty("journalLineId").GetGuid())];

        plain.Rows.ShouldNotBeEmpty();
        LineIds(accrual).ShouldBe(LineIds(plain), ignoreOrder: false,
            "every trust_bank line is posted 'both', so both bases select identical rows");
    }

    [Fact]
    public async Task Preview_bank_rec_returns_rows_for_seeded_finalized_reconciliation()
    {
        // WP-6 (M6): the demo seed now includes a finalized BankReconciliation for the Operating
        // Trust, April 2026 (see DemoBankClearingSeed.EnsureFinalizedReconciliationAsync). The
        // bank-rec preview must return at least one row with the expected period and balance.
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var result = await GetAsync<PreviewSpaResponse>(client, "/api/reports/bank-rec/preview", ct);

        result.Rows.ShouldNotBeEmpty("the seeded April 2026 finalized reconciliation should produce at least one row");
        result.Message.ShouldBeNull("a non-empty result should carry no message");

        // The first row should contain the April 2026 period data.
        var rows = result.Rows.OfType<System.Text.Json.JsonElement>().ToList();
        rows.ShouldNotBeEmpty();
        var first = rows[0];
        first.GetProperty("year").GetInt32().ShouldBe(2026);
        first.GetProperty("month").GetInt32().ShouldBe(4);
        first.GetProperty("statementEndingBalance").GetDecimal().ShouldBe(250_450.14m);
    }

    [Fact]
    public async Task Preview_owner_stmt_returns_redirect_message()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var result = await GetAsync<PreviewSpaResponse>(client, "/api/reports/owner-stmt/preview", ct);

        result.Rows.ShouldBeEmpty();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("/api/statements/");
    }

    [Fact]
    public async Task Preview_unknown_report_returns_404()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var response = await client.GetAsync("/api/reports/nonexistent-report/preview", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ─── auth guard ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unauthenticated_catalog_request_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        var anonClient = fixture.Api.CreateClient();
        // No login — just prime CSRF for the request (not needed for GET, but realistic).
        var response = await anonClient.GetAsync("/api/reports", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ─── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Logs in as the demo admin (seeds on first call, idempotent thereafter).</summary>
    private async Task<HttpClient> DemoClientAsync(CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(DemoSeeder.AdminEmail, DemoSeeder.AdminPassword), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.PrimeCsrfAsync(ct);
        return client;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url, CancellationToken ct)
    {
        var response = await client.GetAsync(url, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            $"GET {url} failed: " + await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }
}
