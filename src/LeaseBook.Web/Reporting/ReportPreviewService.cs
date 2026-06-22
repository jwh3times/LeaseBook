using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Reporting;
using LeaseBook.Modules.Reporting.Catalog;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// Host-owned preview dispatcher (§M5 / ADR-016): given a report id and filter bag, runs the
/// appropriate query via <see cref="ISender"/> and returns a generic rows payload the SPA renders.
/// Lives in the host because it legitimately dispatches across Accounting + Directory modules
/// (the composition-root pattern, same as <c>DashboardService</c>). The Reporting module supplies
/// the catalog and statement assembler; the host supplies the cross-module dispatch.
/// </summary>
public sealed class ReportPreviewService(ISender sender)
{
    /// <summary>
    /// Runs the named report with the provided filters and returns a generic preview result.
    /// Returns null when the report id is not found in the catalog.
    /// </summary>
    public async Task<ReportPreviewResult?> PreviewAsync(
        string reportId, ReportFilters filters, CancellationToken ct)
    {
        var descriptor = ReportCatalog.Find(reportId);
        if (descriptor is null)
        {
            return null;
        }

        return reportId switch
        {
            "owner-bal" => await PreviewOwnerBalancesAsync(descriptor, ct),
            "rent-roll" => await PreviewRentRollAsync(descriptor, ct),
            "delinquency" => await PreviewDelinquencyAsync(descriptor, filters, ct),
            "mgmt-fee" => await PreviewMgmtFeeAsync(descriptor, filters, ct),
            _ => new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category,
                "Preview not yet implemented for this report type. Use the dedicated endpoint for full output.",
                []),
        };
    }

    // --- per-report preview implementations ---

    private async Task<ReportPreviewResult> PreviewOwnerBalancesAsync(
        ReportDescriptor descriptor, CancellationToken ct)
    {
        var response = await sender.Query(new GetOwnerBalances(), ct);
        var rows = response.Rows.Select(r => new Dictionary<string, object?>
        {
            ["ownerId"] = r.OwnerId,
            ["operating"] = r.Operating,
            ["deposits"] = r.Deposits,
            ["total"] = r.Total,
        }).ToList<object>();

        return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category, null, rows);
    }

    private async Task<ReportPreviewResult> PreviewRentRollAsync(
        ReportDescriptor descriptor, CancellationToken ct)
    {
        var response = await sender.Query(new GetRentRoll(), ct);
        var rows = response.Rows.Select(r => new Dictionary<string, object?>
        {
            ["unitId"] = r.UnitId,
            ["property"] = r.Property,
            ["tenant"] = r.Tenant,
            ["rent"] = r.Rent,
            ["status"] = r.Status,
        }).ToList<object>();

        return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category, null, rows);
    }

    private async Task<ReportPreviewResult> PreviewDelinquencyAsync(
        ReportDescriptor descriptor, ReportFilters filters, CancellationToken ct)
    {
        var asOf = filters.AsOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var response = await sender.Query(new GetDelinquencyAging(asOf), ct);
        var rows = response.Rows.Select(r => new Dictionary<string, object?>
        {
            ["tenantId"] = r.TenantId,
            ["current"] = r.Current,
            ["d1_30"] = r.D1_30,
            ["d31_60"] = r.D31_60,
            ["d61_90"] = r.D61_90,
            ["over90"] = r.Over90,
            ["total"] = r.Total,
        }).ToList<object>();

        return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category, null, rows);
    }

    private async Task<ReportPreviewResult> PreviewMgmtFeeAsync(
        ReportDescriptor descriptor, ReportFilters filters, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var year = filters.Year ?? now.Year;
        var month = filters.Month ?? now.Month;

        var response = await sender.Query(new GetManagementFeeIncome(year, month), ct);
        var rows = response.Rows.Select(r => new Dictionary<string, object?>
        {
            ["propertyId"] = r.PropertyId,
            ["amount"] = r.Amount,
        }).ToList<object>();

        return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category, null, rows);
    }
}

/// <summary>Filter bag for report preview requests.</summary>
public sealed record ReportFilters(
    int? Year = null,
    int? Month = null,
    Guid? OwnerId = null,
    Guid? PropertyId = null,
    Guid? BankAccountId = null,
    DateOnly? AsOf = null);

/// <summary>Generic preview result — rows is a list of dictionaries for flexible SPA rendering.</summary>
public sealed record ReportPreviewResult(
    string ReportId,
    string Name,
    string Category,
    string? Message,
    IReadOnlyList<object> Rows);
