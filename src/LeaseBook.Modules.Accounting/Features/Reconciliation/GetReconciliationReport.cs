using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Persistence;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Reconciliation;

/// <summary>Returns one reconciliation's stored report snapshot verbatim — never recomputed (P64).</summary>
public sealed record GetReconciliationReport(Guid ReconciliationId) : IQuery<ReconciliationReportResponse?>;

public sealed record ReconciliationReportResponse(Guid Id, string Status, string? ReportJson);

internal sealed class GetReconciliationReportHandler(DbContext db)
    : IQueryHandler<GetReconciliationReport, ReconciliationReportResponse?>
{
    public async Task<ReconciliationReportResponse?> Handle(GetReconciliationReport query, CancellationToken ct)
    {
        var recon = await db.Set<BankReconciliation>().AsNoTracking()
            .Where(r => r.Id == query.ReconciliationId)
            .Select(r => new { r.Id, r.Status, r.ReportSnapshot })
            .FirstOrDefaultAsync(ct);

        return recon is null
            ? null
            : new ReconciliationReportResponse(recon.Id, ReconciliationStatusConverter.ToDb(recon.Status), recon.ReportSnapshot);
    }
}
