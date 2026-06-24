using System.Text;
using LeaseBook.Modules.Reporting.Catalog;
using LeaseBook.Modules.Reporting.Rendering;
using LeaseBook.Web.Reporting;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// Pure-render guard (no database): every CSV export neutralizes spreadsheet formula injection in
/// its free-text cells. Complements the SharedKernel <c>CsvFormulaGuard</c> unit tests by proving
/// each writer actually routes its cells through the guard. Not in the database collection — these
/// construct view models directly, so they need no Postgres fixture.
/// </summary>
public sealed class CsvInjectionTests
{
    private const string Payload = "=cmd|'/c calc'!A1";

    [Fact]
    public void StatementCsv_neutralizes_formula_injection_in_line_description_and_property()
    {
        var csv = Encoding.UTF8.GetString(StatementCsv.Write(StatementWithPayload()));

        csv.ShouldContain("'=cmd");    // apostrophe-guarded → treated as text by a spreadsheet
        csv.ShouldNotContain(",=cmd");  // never an unguarded formula at a field boundary
    }

    [Fact]
    public void ReportCsv_neutralizes_formula_injection_in_data_cells()
    {
        var descriptor = ReportCatalog.All[0];
        var csv = Encoding.UTF8.GetString(ReportCsv.Write(descriptor, ["value"], [[Payload]]));

        csv.ShouldContain("'=cmd");
        csv.ShouldNotContain(",=cmd");
    }

    private static StatementView StatementWithPayload()
    {
        var branding = new LeaseBook.Modules.Reporting.Contracts.PmBrandingRow(
            CompanyName: "Harbour Front PM", LogoBlobRef: null, ParenthesizedNegatives: false);

        var lines = new List<StatementLineView>
        {
            new(Guid.NewGuid(), new DateOnly(2026, 5, 1), "RentCharged", null, Payload, Payload, 1_000.00m),
        };
        var sections = new List<StatementSectionView> { new("income", "Income", lines, 1_000.00m) };
        var fiduciary = new FiduciaryPanel(
            Balanced: true, Variance: 0m, PmIncomeExcluded: true,
            DepositsRecognizedOnApplication: true, LatestReconciledBank: null);

        return new StatementView(
            OwnerId: Guid.NewGuid(), OwnerName: "Acme", PropertyAddress: "1 Main St",
            Basis: "cash", Year: 2026, Month: 5, Beginning: 0m, Sections: sections,
            Ending: 1_000.00m, Fiduciary: fiduciary, Branding: branding);
    }
}
