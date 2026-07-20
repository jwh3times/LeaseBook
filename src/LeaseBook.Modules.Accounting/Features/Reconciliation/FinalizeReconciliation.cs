using System.Text.Json;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Reconciliation;

/// <summary>
/// Finalizes a reconciliation (P64): requires a zero difference, marks the account's cleared lines
/// <c>reconciled</c> (stamped with this reconciliation), stores an immutable report snapshot, and locks
/// the (account, month) against further bank postings. A non-zero difference is rejected.
/// </summary>
public sealed record FinalizeReconciliation(Guid ReconciliationId) : ICommand<ReconciliationView>;

internal sealed class FinalizeReconciliationHandler(DbContext db, IActorContext? actor = null)
    : ICommandHandler<FinalizeReconciliation, ReconciliationView>
{
    public async Task<ReconciliationView> Handle(FinalizeReconciliation command, CancellationToken ct)
    {
        var recon = await db.Set<BankReconciliation>().FirstOrDefaultAsync(r => r.Id == command.ReconciliationId, ct)
            ?? throw new ReconciliationNotFoundException(command.ReconciliationId);

        if (recon.Status == ReconciliationStatus.Finalized)
        {
            throw new ReconciliationStateException(ReconciliationStateProblem.AlreadyFinalized, recon.Id);
        }

        var cleared = await ReconciliationSql.ClearedBalanceAsync(db, recon.BankAccountId, ct);
        var difference = recon.StatementEndingBalance.Amount - cleared;
        if (difference != 0m)
        {
            throw new ReconciliationUnbalancedException(difference);
        }

        // Lock the account's currently-cleared lines into this reconciliation.
        var reconciledCount = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE bank_line_status s
            SET status = 'reconciled', reconciliation_id = {recon.Id}, updated_at = now()
            FROM journal_lines jl
            WHERE s.journal_line_id = jl.id
              AND jl.bank_account_id = {recon.BankAccountId}
              AND jl.account_class IN ('trust_bank', 'pm_operating_bank')
              AND s.status = 'cleared'
            """, ct);

        var snapshot = JsonSerializer.Serialize(new
        {
            reconciliationId = recon.Id,
            bankAccountId = recon.BankAccountId,
            period = $"{recon.PeriodYear}-{recon.PeriodMonth:D2}",
            statementEndingBalance = recon.StatementEndingBalance.Amount,
            clearedBalance = cleared,
            difference = 0m,
            reconciledItemCount = reconciledCount,
            finalizedAt = DateTime.UtcNow,
        });

        recon.Finalize(actor?.UserId, snapshot, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);

        return await ReconciliationSql.BuildAsync(db, recon, ct);
    }
}
