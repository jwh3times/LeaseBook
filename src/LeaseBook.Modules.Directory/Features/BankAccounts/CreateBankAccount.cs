using FluentValidation;
using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.BankAccounts;

/// <summary>
/// Creates a bank account and provisions the matching chart-of-accounts account through the
/// cross-module port (§C.8 / P49). Purpose is immutable after creation: <c>trust</c> is the operating
/// trust account and <c>deposit</c> is the security-deposit trust account (both inside the trust
/// equation); <c>operating</c> is the PM operating account (outside it). No hard delete — a bank with
/// journal history cannot be removed; deactivation: see SetBankAccountActive.
/// </summary>
/// <param name="Purpose">
/// Immutable bank purpose: <c>trust</c>, <c>deposit</c>, or <c>operating</c>, with the meanings above.
/// </param>
public sealed record CreateBankAccount(string Name, string? Institution, string? Mask, string Purpose)
    : ICommand<BankAccountResponse>;

public sealed class CreateBankAccountValidator : AbstractValidator<CreateBankAccount>
{
    public CreateBankAccountValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Institution).MaximumLength(120);
        RuleFor(x => x.Mask).MaximumLength(8);
        RuleFor(x => x.Purpose).Must(v => BankPurposeConverter.DbValues.Contains(v))
            .WithMessage($"Purpose must be one of: {string.Join(", ", BankPurposeConverter.DbValues)}.");
    }
}

internal sealed class CreateBankAccountHandler(DbContext db, IChartProvisioner chartProvisioner)
    : ICommandHandler<CreateBankAccount, BankAccountResponse>
{
    public async Task<BankAccountResponse> Handle(CreateBankAccount command, CancellationToken ct)
    {
        var purpose = BankPurposeConverter.FromDb(command.Purpose);
        var bank = new BankAccount
        {
            Id = UuidV7.NewId(),
            Name = command.Name,
            Institution = command.Institution,
            Mask = command.Mask,
            Purpose = purpose,
        };

        db.Set<BankAccount>().Add(bank);
        await db.SaveChangesAsync(ct);

        // Provision the matching accounting account (idempotent by code) on the same org transaction.
        await chartProvisioner.ProvisionBankAccountAsync(bank.Id, bank.Name, purpose, ct);

        return BankAccountResponse.From(bank);
    }
}
