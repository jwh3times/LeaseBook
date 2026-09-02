using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Reporting;
using LeaseBook.Modules.Reporting.Catalog;
using LeaseBook.Modules.Reporting.Rendering;
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
                "Owner statements have a dedicated endpoint: GET /api/statements/{ownerId}", EmptyTable),
            "owner-bal" => await PreviewOwnerBalancesAsync(descriptor, filters, ct),
            "rent-roll" => await PreviewRentRollAsync(descriptor, ct),
            "delinquency" => await PreviewDelinquencyAsync(descriptor, filters, ct),
            "mgmt-fee" => await PreviewMgmtFeeAsync(descriptor, filters, ct),
            "deposit-liab" => await PreviewDepositLiabAsync(descriptor, ct),
            "trust-ledger" => await PreviewTrustLedgerAsync(descriptor, filters, ct),
            "bank-rec" => await PreviewBankRecAsync(descriptor, filters, ct),
            _ => new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category,
                "Preview not yet implemented for this report type. Use the dedicated endpoint for full output.",
                EmptyTable),
        };
    }

    // --- per-report preview implementations ---

    /// <summary>
    /// The only basis-aware preview. Owner equity is credited on a different event per basis
    /// (<c>RentCharged</c> accrual / <c>PaymentReceived</c> cash), so Operating genuinely differs;
    /// Deposits does not, because every <c>deposit_liability</c> line is tagged <c>both</c>. The
    /// resolved basis is echoed back rather than trusted from the client.
    /// </summary>
    private async Task<ReportPreviewResult> PreviewOwnerBalancesAsync(
        ReportDescriptor descriptor, ReportFilters filters, CancellationToken ct)
    {
        var basis = ResolveBasis(filters.Basis);
        var response = await sender.Query(new GetOwnerBalances(basis), ct);
        var table = ReportTable.Project(response.Rows,
            new ReportColumn<OwnerBalanceRow>("ownerId", row => row.OwnerId),
            new ReportColumn<OwnerBalanceRow>("operating", row => row.Operating),
            new ReportColumn<OwnerBalanceRow>("deposits", row => row.Deposits),
            new ReportColumn<OwnerBalanceRow>("total", row => row.Total));

        return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category, null, table, basis,
            [new ReportCsvMetadata("basis", basis)]);
    }

    /// <summary>Same normalization the statement endpoints use: anything but "accrual" is cash.</summary>
    private static string ResolveBasis(string? requested) =>
        requested?.ToLowerInvariant() is "accrual" ? "accrual" : "cash";

    private async Task<ReportPreviewResult> PreviewRentRollAsync(
        ReportDescriptor descriptor, CancellationToken ct)
    {
        var response = await sender.Query(new GetRentRoll(), ct);
        var table = ReportTable.Project(response.Rows,
            new ReportColumn<RentRollRow>("unitId", row => row.UnitId),
            new ReportColumn<RentRollRow>("property", row => row.Property),
            new ReportColumn<RentRollRow>("tenant", row => row.Tenant),
            new ReportColumn<RentRollRow>("rent", row => row.Rent),
            new ReportColumn<RentRollRow>("occupancy", row => row.Occupancy),
            new ReportColumn<RentRollRow>("availability", row => row.Availability));

        return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category, null, table);
    }

    private async Task<ReportPreviewResult> PreviewDelinquencyAsync(
        ReportDescriptor descriptor, ReportFilters filters, CancellationToken ct)
    {
        var asOf = filters.AsOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var response = await sender.Query(new GetDelinquencyAging(asOf), ct);
        var table = ReportTable.Project(response.Rows,
            new ReportColumn<DelinquencyRow>("tenantId", row => row.TenantId),
            new ReportColumn<DelinquencyRow>("current", row => row.Current),
            new ReportColumn<DelinquencyRow>("d1_30", row => row.D1_30),
            new ReportColumn<DelinquencyRow>("d31_60", row => row.D31_60),
            new ReportColumn<DelinquencyRow>("d61_90", row => row.D61_90),
            new ReportColumn<DelinquencyRow>("over90", row => row.Over90),
            new ReportColumn<DelinquencyRow>("total", row => row.Total),
            new ReportColumn<DelinquencyRow>("unappliedCredit", row => row.UnappliedCredit));

        return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category, null, table,
            AppliedFilters: [new ReportCsvMetadata("asOf", asOf.ToString("yyyy-MM-dd"))]);
    }

    private async Task<ReportPreviewResult> PreviewMgmtFeeAsync(
        ReportDescriptor descriptor, ReportFilters filters, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var year = filters.Year ?? now.Year;
        var month = filters.Month ?? now.Month;

        var response = await sender.Query(new GetManagementFeeIncome(year, month), ct);
        var table = ReportTable.Project(response.Rows,
            new ReportColumn<MgmtFeeIncomeRow>("propertyId", row => row.PropertyId),
            new ReportColumn<MgmtFeeIncomeRow>("amount", row => row.Amount));

        return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category, null, table,
            AppliedFilters:
            [
                new ReportCsvMetadata("year", year.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new ReportCsvMetadata("month", month.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ]);
    }

    private async Task<ReportPreviewResult> PreviewDepositLiabAsync(
        ReportDescriptor descriptor, CancellationToken ct)
    {
        var response = await sender.Query(new GetDepositRegister(), ct);
        var table = ReportTable.Project(response.Rows,
            new ReportColumn<DepositRegisterRow>("tenantId", row => row.TenantId),
            new ReportColumn<DepositRegisterRow>("kind", row => row.Kind),
            new ReportColumn<DepositRegisterRow>("held", row => row.Held));

        return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category, null, table);
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
                "No trust bank account found for this org.", TrustLedgerTable([]));
        }

        // Preview is a sample (first page, up to 50 rows) — not the full ledger.
        var response = await sender.Query(new GetBankRegister(bankId.Value, PageSize: 50), ct);
        var table = TrustLedgerTable(response.Rows);

        return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category, null, table,
            AppliedFilters: [new ReportCsvMetadata("bankAccountId", bankId.Value.ToString())]);
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
                "No trust bank account found for this org.", ReconciliationTable([]));
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
                "No finalized reconciliation found for this bank account.", ReconciliationTable([]),
                AppliedFilters: [new ReportCsvMetadata("bankAccountId", bankId.Value.ToString())]);
        }

        var table = ReconciliationTable(finalized);

        return new ReportPreviewResult(descriptor.Id, descriptor.Name, descriptor.Category, null, table,
            AppliedFilters: [new ReportCsvMetadata("bankAccountId", bankId.Value.ToString())]);
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

    private static ReportTable TrustLedgerTable(IReadOnlyList<RegisterRow> rows) =>
        ReportTable.Project(rows,
            new ReportColumn<RegisterRow>("journalLineId", row => row.JournalLineId),
            new ReportColumn<RegisterRow>("date", row => row.Date),
            new ReportColumn<RegisterRow>("description", row => row.Description),
            new ReportColumn<RegisterRow>("deposit", row => row.Deposit),
            new ReportColumn<RegisterRow>("withdrawal", row => row.Withdrawal),
            new ReportColumn<RegisterRow>("status", row => row.Status.ToString()));

    private static ReportTable ReconciliationTable(IReadOnlyList<ReconciliationSummary> rows) =>
        ReportTable.Project(rows,
            new ReportColumn<ReconciliationSummary>("bankAccountId", row => row.BankAccountId),
            new ReportColumn<ReconciliationSummary>("year", row => row.Year),
            new ReportColumn<ReconciliationSummary>("month", row => row.Month),
            new ReportColumn<ReconciliationSummary>("statementEndingBalance", row => row.StatementEndingBalance),
            new ReportColumn<ReconciliationSummary>("finalizedAt", row => row.FinalizedAt));

    private static readonly ReportTable EmptyTable = new([], [], []);
}
