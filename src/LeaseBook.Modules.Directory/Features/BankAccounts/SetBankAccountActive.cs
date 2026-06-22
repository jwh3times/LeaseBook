using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.BankAccounts;

/// <summary>
/// Activates or deactivates a bank account (M2-E9). Reactivation is always allowed; deactivation is
/// blocked while the account has uncleared items (checked through the Accounting clearance port,
/// ADR-007) so an inactive account never hides outstanding work. Deactivation only removes the account
/// from new-posting pickers (ListBankAccounts(ActiveOnly: true)); read/reconcile surfaces are unchanged.
/// </summary>
public sealed record SetBankAccountActive(Guid Id, bool IsActive) : ICommand<SetActiveResult>;

public enum SetActiveOutcome
{
    Updated,
    NotFound,
    BlockedUncleared,
}

public sealed record SetActiveResult(SetActiveOutcome Outcome, BankAccountResponse? Bank);

internal sealed class SetBankAccountActiveHandler(DbContext db, IBankClearanceStatus clearance)
    : ICommandHandler<SetBankAccountActive, SetActiveResult>
{
    public async Task<SetActiveResult> Handle(SetBankAccountActive command, CancellationToken ct)
    {
        var bank = await db.Set<BankAccount>().FirstOrDefaultAsync(b => b.Id == command.Id, ct);
        if (bank is null)
        {
            return new SetActiveResult(SetActiveOutcome.NotFound, null);
        }

        if (!command.IsActive)
        {
            var uncleared = await clearance.UnclearedCountsAsync(ct);
            if (uncleared.GetValueOrDefault(bank.Id) > 0)
            {
                return new SetActiveResult(SetActiveOutcome.BlockedUncleared, null);
            }
        }

        bank.IsActive = command.IsActive;
        await db.SaveChangesAsync(ct);
        return new SetActiveResult(SetActiveOutcome.Updated, BankAccountResponse.From(bank));
    }
}
