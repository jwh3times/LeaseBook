using System.Text.Json;
using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Reconciliation;

/// <summary>
/// Finalizes a reconciliation (P64) at a zero difference against the month's own statement (#656). The
/// month's cleared balance is what the account's reconciliations for it or an earlier month stamped, plus
/// the lines assigned to it now. <paramref name="AssignedLineIds"/> names those lines, cleared or not: each
/// must be an unreconciled bank line of the account dated through the month's last day, and an uncleared
/// one is cleared. Without it, every cleared, unreconciled line dated through the month's last day is
/// assigned. Finalize stamps only the assigned lines, stores the report, and locks the (account, month)
/// against further bank postings.
/// </summary>
public sealed record FinalizeReconciliation(Guid ReconciliationId, IReadOnlyCollection<Guid>? AssignedLineIds = null)
    : ICommand<ReconciliationView>;

/// <summary>The finalize route's optional body: the lines to assign to the month, cleared or not.</summary>
public sealed record FinalizeReconciliationRequest(IReadOnlyList<Guid>? AssignedLineIds = null);

public sealed class FinalizeReconciliationValidator : AbstractValidator<FinalizeReconciliation>
{
    public FinalizeReconciliationValidator() => RuleForEach(x => x.AssignedLineIds).NotEmpty();
}

internal sealed class FinalizeReconciliationHandler(
    DbContext db, IActorContext actor, IOrgContext tenant, PostingCalendar calendar, IPostingLock postingLock)
    : ICommandHandler<FinalizeReconciliation, ReconciliationView>
{
    public async Task<ReconciliationView> Handle(FinalizeReconciliation command, CancellationToken ct)
    {
        // Serialized with the org's other reconciliation commands and guarded postings, so two finalizes,
        // or an unlock and a finalize, cannot each read the months' order or the lines' state stale (#656).
        await postingLock.AcquireAsync(ct);

        var recon = await db.Set<BankReconciliation>().FirstOrDefaultAsync(r => r.Id == command.ReconciliationId, ct)
            ?? throw new ReconciliationNotFoundException(command.ReconciliationId);

        if (recon.Status == ReconciliationStatus.Finalized)
        {
            throw new ReconciliationStateException(ReconciliationStateProblem.AlreadyFinalized, recon.Id);
        }

        await ReconciliationOrder.EnsureNotFutureAsync(calendar, recon.PeriodYear, recon.PeriodMonth, ct);
        await ReconciliationOrder.EnsureNoLaterFinalizedAsync(
            db, recon.BankAccountId, recon.PeriodYear, recon.PeriodMonth, ReconciliationAction.Finalize, ct);
        await ReconciliationOrder.EnsureNoEarlierReopenedAsync(db, recon.BankAccountId, recon.PeriodYear, recon.PeriodMonth, ct);

        // Two different nulls live here and only one of them is legitimate. A system actor has no
        // human to record, so FinalizedBy stays nullable — that is a meaningful answer. No declared
        // actor at all is not: finalizing locks the period, and `actor?.UserId` recorded both cases
        // as the same null. Fail closed on the second, exactly as PostingService does (ADR-039).
        var declared = actor.Actor
            ?? throw new InvalidOperationException(
                "Finalizing a reconciliation requires a declared actor — Actor.User(id) for a signed-in " +
                "user, Actor.System(process) for a named process. Finalizing locks the (account, month) " +
                "against further bank postings (ADR-039), so it is refused rather than recorded null.");

        var today = await calendar.TodayAsync(ct);
        if (command.AssignedLineIds is null && !ReconciliationSql.IsCurrentMonth(recon, today))
        {
            throw new ReconciliationLinesRequiredException(recon.PeriodYear, recon.PeriodMonth, today);
        }

        var assigned = command.AssignedLineIds is null
            ? await ReconciliationSql.DefaultAssignmentAsync(db, recon, ct)
            : await NamedAssignmentAsync(recon, command.AssignedLineIds, ct);

        var cleared = await ReconciliationSql.ReconciledThroughAsync(db, recon.BankAccountId, recon.PeriodYear, recon.PeriodMonth, ct)
            + assigned.Sum(l => l.Amount);
        var difference = recon.StatementEndingBalance.Amount - cleared;
        if (difference != 0m)
        {
            throw new ReconciliationUnbalancedException(difference);
        }

        var ids = assigned.Select(l => l.JournalLineId).ToArray();
        var uncleared = assigned.Where(l => l.Status != "cleared").Select(l => l.JournalLineId).ToArray();
        if (uncleared.Length > 0)
        {
            // Assigning an uncleared line is ticking it: it was paid on this statement.
            await new ApplyClearancesHandler(db, tenant).Handle(new ApplyClearances(uncleared), ct);
        }

        // Stamp only the assigned lines into this reconciliation.
        var reconciledCount = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE bank_line_status
            SET status = 'reconciled', reconciliation_id = {recon.Id}, updated_at = now()
            WHERE journal_line_id = ANY({ids}) AND status = 'cleared'
            """, ct);
        if (reconciledCount != ids.Length)
        {
            // Every assigned line was cleared and unreconciled a moment ago, under the lock. A short count
            // means the month would finalize against lines it did not stamp: refuse, and roll back.
            throw new InvalidOperationException(
                $"Finalizing reconciliation {recon.Id} stamped {reconciledCount} of {ids.Length} assigned lines.");
        }

        var now = DateTime.UtcNow;
        var report = new Dictionary<string, object?>
        {
            ["reconciliationId"] = recon.Id,
            ["bankAccountId"] = recon.BankAccountId,
            ["period"] = $"{recon.PeriodYear}-{recon.PeriodMonth:D2}",
            ["statementEndingBalance"] = recon.StatementEndingBalance.Amount,
            ["clearedBalance"] = cleared,
            ["difference"] = 0m,
            ["reconciledItemCount"] = reconciledCount,
            ["finalizedAt"] = now,
        };
        if (recon.Status == ReconciliationStatus.Reopened)
        {
            // Finalizing again replaces the report; the one replaced stays in the audit trail's
            // before-image. This one says when the month first closed, who reopened it and why (#656).
            // A month reopened before the first-finalized time was kept still has its last finalize time.
            report["firstFinalizedAt"] = recon.FirstFinalizedAt ?? recon.FinalizedAt;
            report["reopenedBy"] = recon.ReopenedBy;
            report["reopenReason"] = recon.ReopenReason;
            report["finalizedAgainAt"] = now;
        }

        recon.Finalize(declared.UserId, JsonSerializer.Serialize(report), now);
        await db.SaveChangesAsync(ct);

        return await ReconciliationSql.BuildAsync(db, recon, today, ct);
    }

    /// <summary>
    /// The lines the caller named, widened to whole payouts (ADR-053), each checked assignable: an
    /// unreconciled bank line of the account dated through the month's last day.
    /// </summary>
    private async Task<List<ReconciliationLine>> NamedAssignmentAsync(
        BankReconciliation recon, IReadOnlyCollection<Guid> named, CancellationToken ct)
    {
        var (year, month) = (recon.PeriodYear, recon.PeriodMonth);
        var monthEnd = ReconciliationSql.MonthEnd(year, month);
        var ids = await PayoutLines.WithWholePayoutsAsync(db, named.Distinct().ToArray(), ct);

        // Read the named lines without the month bound, so a line dated after it is named as such.
        var lines = await ReconciliationSql.UnreconciledByIdAsync(db, recon.BankAccountId, ids, ct);
        if (lines.Count != ids.Length)
        {
            var reconciled = await db.Set<BankLineState>().AnyAsync(
                s => ids.Contains(s.JournalLineId) && s.Status == BankLineStatus.Reconciled, ct);
            throw new ReconciliationLineNotAssignableException(
                reconciled ? ReconciliationLineProblem.AlreadyReconciled : ReconciliationLineProblem.NotOnAccount, year, month);
        }

        var late = lines.Where(l => l.EntryDate > monthEnd).OrderBy(l => l.EntryDate).FirstOrDefault();
        if (late is not null)
        {
            throw new ReconciliationLineNotAssignableException(
                ReconciliationLineProblem.AfterMonthEnd, year, month, late.EntryDate);
        }

        return lines;
    }
}
