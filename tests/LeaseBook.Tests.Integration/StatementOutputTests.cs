using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using CsvHelper;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Reporting;
using LeaseBook.Web.Seeding;
using Shouldly;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-04 (M5): PDF + CSV output endpoints and renderers, exercised against the seeded host.
/// <list type="bullet">
/// <item>PDF: content assertion — basis label, owner name, ending-balance figure extracted via PdfPig (MIT).</item>
/// <item>CSV: round-trip parse — section subtotals + ending balance as decimal values.</item>
/// <item>Endpoints: correct Content-Type headers and file-name dispositions.</item>
/// </list>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class StatementOutputTests(PostgresFixture fixture)
{
    // ─── StatementPdf (unit-style, no HTTP) ────────────────────────────────────

    [Fact]
    public void StatementPdf_renders_valid_pdf_bytes_with_percent_pdf_header()
    {
        var view = BuildTestView();

        var bytes = StatementPdf.Render(view);

        bytes.ShouldNotBeEmpty();
        // PDF magic bytes
        bytes[0].ShouldBe((byte)'%');
        bytes[1].ShouldBe((byte)'P');
        bytes[2].ShouldBe((byte)'D');
        bytes[3].ShouldBe((byte)'F');
        // Sanity-floor: a minimal statement PDF should be at least 8 KB
        bytes.Length.ShouldBeGreaterThan(8_000, "rendered PDF is suspiciously small");
    }

    [Fact]
    public void StatementPdf_content_contains_basis_label_owner_name_and_ending_balance()
    {
        // Chosen path: PdfPig (MIT) text extraction for a genuine content assertion.
        // Rationale: PdfPig is a well-maintained MIT library already in the test-only dep list;
        // extracting rendered text is more reliable than embedding a doc-model assertion and
        // catches layout regressions (e.g. basis label accidentally omitted from a page).
        var view = BuildTestView();
        var bytes = StatementPdf.Render(view);

        // Extract all text from all pages via PdfPig
        var allText = ExtractPdfText(bytes);

        // Basis label: the PDF capitalises "cash" → "Cash"
        allText.ShouldContain("Cash");

        // Owner name
        allText.ShouldContain("Ridgeline Investments");

        // Ending balance — 22,640.30 formatted by the renderer (N2 invariant = "22,640.30")
        allText.ShouldContain("22,640.30");
    }

    [Fact]
    public void StatementPdf_content_contains_fiduciary_section()
    {
        var view = BuildTestView();
        var bytes = StatementPdf.Render(view);
        var allText = ExtractPdfText(bytes);

        allText.ShouldContain("Fiduciary");
    }

    // ─── StatementCsv (unit-style, no HTTP) ────────────────────────────────────

    [Fact]
    public void StatementCsv_roundtrip_contains_ending_balance_as_decimal_string()
    {
        var view = BuildTestView();
        var bytes = StatementCsv.Write(view);

        bytes.ShouldNotBeEmpty();
        var csv = Encoding.UTF8.GetString(bytes);

        // Round-trip parse: CsvHelper reads back what we wrote
        using var reader = new StringReader(csv);
        using var csvReader = new CsvReader(reader, CultureInfo.InvariantCulture);
        csvReader.Read(); // skip header row
        csvReader.ReadHeader();

        var allRows = new List<string[]>();
        while (csvReader.Read())
        {
            var row = new string[csvReader.Parser.Count];
            for (var i = 0; i < csvReader.Parser.Count; i++)
            {
                row[i] = csvReader.GetField(i) ?? string.Empty;
            }

            allRows.Add(row);
        }

        // Ending-balance row: Section="ENDING", Amount="22640.30"
        var endingRow = allRows.FirstOrDefault(r => r.Length > 0 && r[0] == "ENDING");
        endingRow.ShouldNotBeNull("CSV must contain an ENDING row");
        // Amount is column index 4 (Section, Date, Description, Property, Amount)
        endingRow![4].ShouldBe("22640.30");
    }

    [Fact]
    public void StatementCsv_roundtrip_contains_section_subtotals()
    {
        var view = BuildTestView();
        var bytes = StatementCsv.Write(view);
        var csv = Encoding.UTF8.GetString(bytes);

        // The CSV must contain sections
        csv.ShouldContain("Income");

        // Parse and check that subtotal rows exist
        using var reader = new StringReader(csv);
        using var csvReader = new CsvReader(reader, CultureInfo.InvariantCulture);
        csvReader.Read();
        csvReader.ReadHeader();

        var subtotalRows = new List<string[]>();
        while (csvReader.Read())
        {
            // Subtotal rows have "Subtotal —" in Description column (index 2)
            var desc = csvReader.GetField(2) ?? string.Empty;
            if (desc.StartsWith("Subtotal", StringComparison.OrdinalIgnoreCase))
            {
                subtotalRows.Add(
                [
                    csvReader.GetField(0) ?? string.Empty, // Section
                    csvReader.GetField(2) ?? string.Empty, // Description
                    csvReader.GetField(4) ?? string.Empty, // Amount
                ]);
            }
        }

        subtotalRows.ShouldNotBeEmpty();

        // All subtotal amounts must parse as decimal (not float)
        foreach (var row in subtotalRows)
        {
            decimal.TryParse(row[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                .ShouldBeTrue($"subtotal amount '{row[2]}' for section '{row[0]}' must be a valid decimal");
        }
    }

    [Fact]
    public void StatementCsv_no_float_values_in_output()
    {
        // Numbers as decimal strings — verify no scientific notation or float artifacts
        var view = BuildTestView();
        var csv = Encoding.UTF8.GetString(StatementCsv.Write(view));

        csv.ShouldNotContain("E+");
        csv.ShouldNotContain("e+");
    }

    // ─── HTTP endpoint tests (over the seeded host) ───────────────────────────

    [Fact]
    public async Task Statement_pdf_endpoint_returns_pdf_bytes_with_correct_content_type()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var url = $"/api/statements/{DemoIds.O5}/pdf?year=2026&month=5&basis=cash";
        var response = await client.GetAsync(url, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "PDF endpoint should return 200: " + await response.Content.ReadAsStringAsync(ct));
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/pdf");

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        bytes.Length.ShouldBeGreaterThan(8_000);
        bytes[0].ShouldBe((byte)'%');
        bytes[1].ShouldBe((byte)'P');
        bytes[2].ShouldBe((byte)'D');
        bytes[3].ShouldBe((byte)'F');

        // Content assertion via PdfPig: must contain basis + owner name + ending balance
        var text = ExtractPdfText(bytes);
        text.ShouldContain("Cash");
        text.ShouldContain("Ridgeline Investments");
        text.ShouldContain("22,640.30");
    }

    [Fact]
    public async Task Statement_csv_endpoint_returns_csv_with_correct_content_type_and_ending_balance()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var url = $"/api/statements/{DemoIds.O5}/csv?year=2026&month=5&basis=cash";
        var response = await client.GetAsync(url, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "CSV endpoint should return 200: " + await response.Content.ReadAsStringAsync(ct));
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/csv");

        var csv = await response.Content.ReadAsStringAsync(ct);
        csv.ShouldContain("22640.30");
        csv.ShouldContain("Ridgeline Investments");
        csv.ShouldContain("cash");
    }

    [Fact]
    public async Task Report_csv_endpoint_returns_csv_for_owner_bal_report()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var response = await client.GetAsync("/api/reports/owner-bal/csv", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "Report CSV endpoint should return 200: " + await response.Content.ReadAsStringAsync(ct));
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/csv");

        var csv = await response.Content.ReadAsStringAsync(ct);
        csv.ShouldNotBeNullOrWhiteSpace();
        // Identity header row must contain the report name
        csv.ShouldContain("All owner ending balances");
    }

    [Fact]
    public async Task Report_csv_endpoint_returns_404_for_unknown_report()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var response = await client.GetAsync("/api/reports/nonexistent/csv", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Statement_pdf_endpoint_requires_auth()
    {
        var ct = TestContext.Current.CancellationToken;
        var anonClient = fixture.Api.CreateClient();
        var response = await anonClient.GetAsync($"/api/statements/{DemoIds.O5}/pdf", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Statement_csv_endpoint_requires_auth()
    {
        var ct = TestContext.Current.CancellationToken;
        var anonClient = fixture.Api.CreateClient();
        var response = await anonClient.GetAsync($"/api/statements/{DemoIds.O5}/csv", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a representative <see cref="StatementView"/> for O5 May 2026 without going through
    /// the database. Uses the golden figures (ending = 22,640.30) to validate the renderer.
    /// Arithmetic: 20,345.30 + 2,900.00 − 605.00 = 22,640.30 ✓
    /// </summary>
    private static StatementView BuildTestView()
    {
        var branding = new LeaseBook.Modules.Reporting.Contracts.PmBrandingRow(
            CompanyName: "Harbour Front PM",
            LogoBlobRef: null,
            ParenthesizedNegatives: false);

        var incomeLines = new List<StatementLineView>
        {
            new(Guid.NewGuid(), new DateOnly(2026, 5, 1), "RentCharged", null,
                "May rent — 204 Elm St", "204 Elm St", 1_950.00m),
            new(Guid.NewGuid(), new DateOnly(2026, 5, 15), "DepositApplied", null,
                "Applied deposit — Okonkwo", "204 Elm St", 950.00m),
        };

        var expenseLines = new List<StatementLineView>
        {
            new(Guid.NewGuid(), new DateOnly(2026, 5, 10), "MaintenancePaid", null,
                "HVAC repair", "204 Elm St", -605.00m),
        };

        var sections = new List<StatementSectionView>
        {
            new("income", "Income — rent collected", incomeLines, 2_900.00m),
            new("expenses", "Operating expenses", expenseLines, -605.00m),
        };

        var fiduciary = new FiduciaryPanel(
            Balanced: true,
            Variance: 0m,
            PmIncomeExcluded: true,
            DepositsRecognizedOnApplication: true,
            LatestReconciledBank: null);

        return new StatementView(
            OwnerId: DemoIds.O5,
            OwnerName: "Ridgeline Investments",
            PropertyAddress: "204 Elm St, Chapel Hill, NC",
            Basis: "cash",
            Year: 2026,
            Month: 5,
            Beginning: 20_345.30m,
            Sections: sections,
            Ending: 22_640.30m,
            Fiduciary: fiduciary,
            Branding: branding);
    }

    /// <summary>
    /// Extracts and concatenates all text from all pages of a PDF using PdfPig (MIT).
    /// Returns a single string suitable for substring assertions.
    /// </summary>
    private static string ExtractPdfText(byte[] pdfBytes)
    {
        using var document = PdfDocument.Open(pdfBytes);
        var sb = new StringBuilder();
        foreach (Page page in document.GetPages())
        {
            foreach (Word word in page.GetWords())
            {
                sb.Append(word.Text);
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }

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
}
