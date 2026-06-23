using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.Modules.Directory.Features.BankAccounts;
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
            "owner-stmt" => new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category,
                "Owner statements have a dedicated endpoint: GET /api/statements/{ownerId}", []),
            "owner-bal" => await PreviewOwnerBalancesAsync(descriptor, ct),
            "rent-roll" => await PreviewRentRollAsync(descriptor, ct),
            "delinquency" => await PreviewDelinquencyAsync(descriptor, filters, ct),
            "mgmt-fee" => await PreviewMgmtFeeAsync(descriptor, filters, ct),
            "deposit-liab" => await PreviewDepositLiabAsync(descriptor, ct),
            "trust-ledger" => await PreviewTrustLedgerAsync(descriptor, filters, ct),
            "bank-rec" => await PreviewBankRecAsync(descriptor, filters, ct),
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

    private async Task<ReportPreviewResult> PreviewDepositLiabAsync(
        ReportDescriptor descriptor, CancellationToken ct)
    {
        var response = await sender.Query(new GetDepositRegister(), ct);
        var rows = response.Rows.Select(r => new Dictionary<string, object?>
        {
            ["tenantId"] = r.TenantId,
            ["kind"] = r.Kind,
            ["held"] = r.Held,
        }).ToList<object>();

        return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category, null, rows);
    }

    private async Task<ReportPreviewResult> PreviewTrustLedgerAsync(
        ReportDescriptor descriptor, ReportFilters filters, CancellationToken ct)
    {
        // Resolve bank account: use the filter if provided; otherwise default to the org's first
        // active trust-purpose bank account so the preview shows real data.
        var bankId = filters.BankAccountId ?? await ResolveTrustBankIdAsync(ct);
        if (bankId is null)
        {
            return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category,
                "No trust bank account found for this org.", []);
        }

        // Preview is a sample (first page, up to 50 rows) — not the full ledger.
        var response = await sender.Query(new GetBankRegister(bankId.Value, PageSize: 50), ct);
        var rows = response.Rows.Select(r => new Dictionary<string, object?>
        {
            ["journalLineId"] = r.JournalLineId,
            ["date"] = r.Date,
            ["description"] = r.Description,
            ["deposit"] = r.Deposit,
            ["withdrawal"] = r.Withdrawal,
            ["status"] = r.Status.ToString(),
        }).ToList<object>();

        return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category, null, rows);
    }

    private async Task<ReportPreviewResult> PreviewBankRecAsync(
        ReportDescriptor descriptor, ReportFilters filters, CancellationToken ct)
    {
        // Resolve bank account: use the filter if provided; otherwise default to the org's first
        // active trust-purpose bank account.
        var bankId = filters.BankAccountId ?? await ResolveTrustBankIdAsync(ct);
        if (bankId is null)
        {
            return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category,
                "No trust bank account found for this org.", []);
        }

        // Reuse the GetReconciliationHistory query (same path as ReconciliationSnapshotsAdapter).
        // Filter to finalized rows for the resolved bank, newest first.
        var history = await sender.Query(new GetReconciliationHistory(bankId), ct);
        var finalized = history.Rows
            .Where(r => r.Status == "finalized" && r.FinalizedAt.HasValue)
            .ToList();

        if (finalized.Count == 0)
        {
            return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category,
                "No finalized reconciliation found for this bank account.", []);
        }

        var rows = finalized.Select(r => new Dictionary<string, object?>
        {
            ["bankAccountId"] = r.BankAccountId,
            ["year"] = r.Year,
            ["month"] = r.Month,
            ["statementEndingBalance"] = r.StatementEndingBalance,
            ["finalizedAt"] = r.FinalizedAt,
        }).ToList<object>();

        return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category, null, rows);
    }

    /// <summary>
    /// Returns the id of the first active trust-purpose bank account for the current org,
    /// or null if none exists. Used as the default when no bankAccountId filter is supplied.
    /// Delegates to Directory's <see cref="ListBankAccounts"/> via <see cref="ISender"/> (ADR-007).
    /// </summary>
    private async Task<Guid?> ResolveTrustBankIdAsync(CancellationToken ct)
    {
        var banks = await sender.Query(new ListBankAccounts(ActiveOnly: true), ct);
        return banks.FirstOrDefault(b => b.Purpose == "trust")?.Id;
    }
}
