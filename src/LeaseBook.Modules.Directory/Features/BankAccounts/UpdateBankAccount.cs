using FluentValidation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.BankAccounts;

/// <summary>
/// Edits a bank account's display fields (§C.4). Editing the name does <b>not</b> rename the provisioned
/// accounting account in M2 — the display name is cosmetic; a rename sync is a later concern (M2-E9).
/// Returns null → 404 when the id is unknown.
/// </summary>
public sealed record UpdateBankAccount(Guid Id, string Name, string? Institution, string? Mask)
    : ICommand<BankAccountResponse?>;

public sealed class UpdateBankAccountValidator : AbstractValidator<UpdateBankAccount>
{
    public UpdateBankAccountValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Institution).MaximumLength(120);
        RuleFor(x => x.Mask).MaximumLength(8);
    }
}

internal sealed class UpdateBankAccountHandler(DbContext db) : ICommandHandler<UpdateBankAccount, BankAccountResponse?>
{
    public async Task<BankAccountResponse?> Handle(UpdateBankAccount command, CancellationToken ct)
    {
        var bank = await db.Set<BankAccount>().FirstOrDefaultAsync(b => b.Id == command.Id, ct);
        if (bank is null)
        {
            return null;
        }

        bank.Name = command.Name;
        bank.Institution = command.Institution;
        bank.Mask = command.Mask;

        await db.SaveChangesAsync(ct);
        return BankAccountResponse.From(bank);
    }
}
