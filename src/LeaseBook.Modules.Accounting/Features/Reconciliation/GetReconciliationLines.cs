using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Reconciliation;

/// <summary>
/// What a month's reconciliation may be finalized against (#656): the balance its account's
/// reconciliations already stamped for it and the months before it, and every unreconciled bank line
/// dated through its last day, cleared or not. The re-finalize view ticks lines from this list and
/// finalize takes the ticked ids, so the difference the screen shows is the one finalize computes:
/// statement − (<see cref="ReconciliationLinesResponse.ReconciledThrough"/> + the ticked amounts).
/// A line of a payout finalize would refuse for the month carries the refusal as its
/// <see cref="ReconciliationLine.NotAssignableReason"/>, and the view does not tick it (#775).
/// Null when no such reconciliation exists in this organization.
/// </summary>
public sealed record GetReconciliationLines(Guid ReconciliationId) : IQuery<ReconciliationLinesResponse?>;

public sealed record ReconciliationLinesResponse(
    ReconciliationView Reconciliation, decimal ReconciledThrough, IReadOnlyList<ReconciliationLine> Lines);

internal sealed class GetReconciliationLinesHandler(DbContext db, PostingCalendar calendar)
    : IQueryHandler<GetReconciliationLines, ReconciliationLinesResponse?>
{
    public async Task<ReconciliationLinesResponse?> Handle(GetReconciliationLines query, CancellationToken ct)
    {
        var recon = await db.Set<BankReconciliation>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == query.ReconciliationId, ct);
        if (recon is null)
        {
            return null;
        }

        var reconciledThrough = await ReconciliationSql.ReconciledThroughAsync(
            db, recon.BankAccountId, recon.PeriodYear, recon.PeriodMonth, ct);
        var lines = await ReconciliationSql.UnreconciledThroughAsync(
            db, recon.BankAccountId, ReconciliationSql.MonthEnd(recon.PeriodYear, recon.PeriodMonth), ct);

        return new ReconciliationLinesResponse(
            await ReconciliationSql.BuildAsync(db, recon, await calendar.TodayAsync(ct), ct),
            reconciledThrough,
            await WithPayoutRefusalsAsync(recon, lines, ct));
    }

    /// <summary>
    /// Asks finalize's own check of each payout on offer (ADR-053), and marks every line of a payout it
    /// would refuse with the reason: a payout with a line dated after the month's last day is not this
    /// month's (#775). A line outside a payout is offered only when it is assignable, so it needs no check.
    /// </summary>
    private async Task<List<ReconciliationLine>> WithPayoutRefusalsAsync(
        BankReconciliation recon, List<ReconciliationLine> lines, CancellationToken ct)
    {
        Dictionary<string, string> refused = new(StringComparer.Ordinal);
        foreach (var payout in lines.Where(l => l.PayoutReference is not null).GroupBy(l => l.PayoutReference!))
        {
            var (_, refusal) = await ReconciliationSql.AssignmentAsync(db, recon, [.. payout.Select(l => l.JournalLineId)], ct);
            if (refusal is not null)
            {
                refused[payout.Key] = refusal.Message;
            }
        }

        return refused.Count == 0
            ? lines
            : [.. lines.Select(l => l.PayoutReference is { } payout && refused.TryGetValue(payout, out var reason)
                ? l with { NotAssignableReason = reason }
                : l)];
    }
}
