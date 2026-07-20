using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Reconciliation;

/// <summary>
/// Reopens a finalized reconciliation (PMAdmin + reason, P64): releases the (account, month) lock so the
/// month can be corrected/re-reconciled. Reconciled items stay reconciled until a re-finalize. The status
/// change is audited with the acting user (the AppDbContext audit pass) and carries the reason.
/// </summary>
public sealed record UnlockReconciliation(Guid ReconciliationId, string Reason) : ICommand<ReconciliationView>;

public sealed class UnlockReconciliationValidator : AbstractValidator<UnlockReconciliation>
{
    public UnlockReconciliationValidator() => RuleFor(x => x.Reason).NotEmpty();
}

internal sealed class UnlockReconciliationHandler(DbContext db) : ICommandHandler<UnlockReconciliation, ReconciliationView>
{
    public async Task<ReconciliationView> Handle(UnlockReconciliation command, CancellationToken ct)
    {
        var recon = await db.Set<BankReconciliation>().FirstOrDefaultAsync(r => r.Id == command.ReconciliationId, ct)
            ?? throw new ReconciliationNotFoundException(command.ReconciliationId);

        if (recon.Status != ReconciliationStatus.Finalized)
        {
            throw new ReconciliationStateException(ReconciliationStateProblem.NotFinalized, recon.Id);
        }

        recon.Reopen(command.Reason);
        await db.SaveChangesAsync(ct);

        return await ReconciliationSql.BuildAsync(db, recon, ct);
    }
}
