using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
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

internal sealed class UnlockReconciliationHandler(
    DbContext db, IActorContext actor, PostingCalendar calendar, IPostingLock postingLock)
    : ICommandHandler<UnlockReconciliation, ReconciliationView>
{
    public async Task<ReconciliationView> Handle(UnlockReconciliation command, CancellationToken ct)
    {
        await postingLock.AcquireAsync(ct);

        var recon = await db.Set<BankReconciliation>().FirstOrDefaultAsync(r => r.Id == command.ReconciliationId, ct)
            ?? throw new ReconciliationNotFoundException(command.ReconciliationId);

        if (recon.Status != ReconciliationStatus.Finalized)
        {
            throw new ReconciliationStateException(ReconciliationStateProblem.NotFinalized, recon.Id);
        }

        // Later months unlock first, so each one's cleared balance takes the correction in turn (#656).
        await ReconciliationOrder.EnsureNoLaterFinalizedAsync(
            db, recon.BankAccountId, recon.PeriodYear, recon.PeriodMonth, ReconciliationAction.Unlock, ct);

        // Who reopened the month is part of the report it is finalized again with (#656), so an
        // undeclared actor is refused rather than recorded as no one, as finalize does (ADR-039).
        var declared = actor.Actor
            ?? throw new InvalidOperationException(
                "Unlocking a reconciliation requires a declared actor — Actor.User(id) for a signed-in " +
                "user, Actor.System(process) for a named process (ADR-039).");

        recon.Reopen(command.Reason, declared.UserId);
        await db.SaveChangesAsync(ct);

        return await ReconciliationSql.BuildAsync(db, recon, await calendar.TodayAsync(ct), ct);
    }
}
