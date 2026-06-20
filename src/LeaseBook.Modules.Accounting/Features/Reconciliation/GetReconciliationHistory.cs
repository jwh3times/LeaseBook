using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Persistence;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Reconciliation;

/// <summary>Reconciliation history, newest first, optionally for one bank account (P64).</summary>
public sealed record GetReconciliationHistory(Guid? BankAccountId = null) : IQuery<ReconciliationHistoryResponse>;

public sealed record ReconciliationHistoryResponse(IReadOnlyList<ReconciliationSummary> Rows);

public sealed record ReconciliationSummary(
    Guid Id, string Status, Guid BankAccountId, int Year, int Month,
    decimal StatementEndingBalance, DateTime? FinalizedAt, bool HasReport);

internal sealed class GetReconciliationHistoryHandler(DbContext db)
    : IQueryHandler<GetReconciliationHistory, ReconciliationHistoryResponse>
{
    public async Task<ReconciliationHistoryResponse> Handle(GetReconciliationHistory query, CancellationToken ct)
    {
        var q = db.Set<BankReconciliation>().AsNoTracking();
        if (query.BankAccountId is { } bankId)
        {
            q = q.Where(r => r.BankAccountId == bankId);
        }

        var rows = await q
            .OrderByDescending(r => r.PeriodYear)
            .ThenByDescending(r => r.PeriodMonth)
            .ThenByDescending(r => r.CreatedAt)
            .Select(r => new
            {
                r.Id,
                r.Status,
                r.BankAccountId,
                r.PeriodYear,
                r.PeriodMonth,
                r.StatementEndingBalance,
                r.FinalizedAt,
                r.ReportSnapshot,
            })
            .ToListAsync(ct);

        var summaries = rows
            .Select(r => new ReconciliationSummary(
                r.Id, ReconciliationStatusConverter.ToDb(r.Status), r.BankAccountId, r.PeriodYear, r.PeriodMonth,
                r.StatementEndingBalance.Amount, r.FinalizedAt, r.ReportSnapshot is not null))
            .ToList();

        return new ReconciliationHistoryResponse(summaries);
    }
}
