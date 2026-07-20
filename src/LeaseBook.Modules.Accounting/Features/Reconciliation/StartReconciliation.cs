using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Reconciliation;

/// <summary>
/// Opens (or re-enters) a reconciliation for an (account, month) with the bank's statement ending
/// balance (P64). Returns the live difference (statement − cleared) the UI drives to $0. A finalized
/// month must be unlocked before it can be reconciled again.
/// </summary>
public sealed record StartReconciliation(Guid BankAccountId, int Year, int Month, decimal StatementEndingBalance)
    : ICommand<ReconciliationView>;

public sealed class StartReconciliationValidator : AbstractValidator<StartReconciliation>
{
    public StartReconciliationValidator()
    {
        RuleFor(x => x.BankAccountId).NotEmpty();
        RuleFor(x => x.Year).InclusiveBetween(2000, 2100);
        RuleFor(x => x.Month).InclusiveBetween(1, 12);
        RuleFor(x => x.StatementEndingBalance).Must(a => decimal.Round(a, 2) == a)
            .WithMessage("The statement ending balance must have at most 2 decimal places.");
    }
}

internal sealed class StartReconciliationHandler(DbContext db) : ICommandHandler<StartReconciliation, ReconciliationView>
{
    public async Task<ReconciliationView> Handle(StartReconciliation command, CancellationToken ct)
    {
        var statement = new Money(command.StatementEndingBalance);
        var recon = await db.Set<BankReconciliation>().FirstOrDefaultAsync(
            r => r.BankAccountId == command.BankAccountId && r.PeriodYear == command.Year && r.PeriodMonth == command.Month, ct);

        if (recon is null)
        {
            recon = BankReconciliation.Start(command.BankAccountId, command.Year, command.Month, statement);
            db.Set<BankReconciliation>().Add(recon);
        }
        else if (recon.Status == ReconciliationStatus.Finalized)
        {
            throw new ReconciliationStateException(
                ReconciliationStateProblem.PeriodFinalized, null, command.Year, command.Month);
        }
        else
        {
            recon.SetStatementBalance(statement);
        }

        await db.SaveChangesAsync(ct);
        return await ReconciliationSql.BuildAsync(db, recon, ct);
    }
}
