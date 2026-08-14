using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// Reattributes every held security-deposit position for a property from the seller to the buyer.
/// Cash does not move; only the owner dimension of the liability changes.
/// </summary>
public sealed record TransferPropertyDeposits(
    Guid PropertyId,
    Guid FromOwnerId,
    Guid ToOwnerId,
    DateOnly EffectiveDate,
    string SourceRef) : ICommand<Guid?>;

public sealed class TransferPropertyDepositsValidator : AbstractValidator<TransferPropertyDeposits>
{
    public TransferPropertyDepositsValidator()
    {
        RuleFor(command => command.PropertyId).NotEmpty();
        RuleFor(command => command.FromOwnerId).NotEmpty();
        RuleFor(command => command.ToOwnerId).NotEmpty().NotEqual(command => command.FromOwnerId);
        RuleFor(command => command.EffectiveDate).NotEmpty();
        RuleFor(command => command.SourceRef).NotEmpty().MaximumLength(200);
    }
}

internal sealed class TransferPropertyDepositsHandler(
    DbContext db,
    IPostingLock postingLock,
    IAccountingPeriods periods,
    IReconciliationLock reconciliationLock,
    IAccountingEvents events)
    : ICommandHandler<TransferPropertyDeposits, Guid?>
{
    public async Task<Guid?> Handle(TransferPropertyDeposits command, CancellationToken ct)
    {
        await postingLock.AcquireAsync(ct);

        var period = await periods.GetOpenPeriodAsync(command.EffectiveDate, ct);
        if (period.Status == PeriodStatus.Closed)
        {
            throw new PeriodClosedException(period.Year, period.Month);
        }

        // A transfer entry can only move the seller's position as it stood on the effective date.
        // If later seller-attributed activity has already changed that bucket, moving only the
        // effective-date balance would strand a residual under the former owner and violate I7.
        // Those later postings must first be reversed and re-posted under effective ownership.
        var hasLaterSellerActivity = await db.Database.SqlQuery<bool>(
            $"""
            SELECT EXISTS (
                SELECT 1
                FROM journal_lines jl
                JOIN journal_entries je ON je.id = jl.entry_id
                JOIN accounts a ON a.id = jl.account_id
                WHERE a.code = 'security_deposits_held'
                  AND jl.property_id = {command.PropertyId}
                  AND jl.owner_id = {command.FromOwnerId}
                  AND je.entry_date > {command.EffectiveDate}
                  AND jl.basis IN ('cash', 'both')
                GROUP BY jl.tenant_id, jl.bank_account_id
                HAVING SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) <> 0
            ) AS "Value"
            """).SingleAsync(ct);
        if (hasLaterSellerActivity)
        {
            throw new ValidationException(
                "Later seller-attributed deposit activity must be reversed before recording this backdated ownership transfer.");
        }

        var positions = await db.Database.SqlQuery<HeldDepositPositionRow>(
            $"""
            SELECT jl.tenant_id, jl.bank_account_id,
                   SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) AS amount
            FROM journal_lines jl
            JOIN journal_entries je ON je.id = jl.entry_id
            JOIN accounts a ON a.id = jl.account_id
            WHERE a.code = 'security_deposits_held'
              AND jl.property_id = {command.PropertyId}
              AND jl.owner_id = {command.FromOwnerId}
              AND je.entry_date <= {command.EffectiveDate}
              AND jl.tenant_id IS NOT NULL
              AND jl.bank_account_id IS NOT NULL
              AND jl.basis IN ('cash', 'both')
            GROUP BY jl.tenant_id, jl.bank_account_id
            HAVING SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) > 0
            """).ToListAsync(ct);

        if (positions.Count == 0)
        {
            return null;
        }

        foreach (var bankAccountId in positions.Select(position => position.BankAccountId).Distinct())
        {
            await reconciliationLock.EnsureOpenAsync(bankAccountId, command.EffectiveDate, ct);
        }

        return await events.PostAsync(
            new DepositResponsibilityTransferred(
                command.PropertyId,
                command.FromOwnerId,
                command.ToOwnerId,
                command.EffectiveDate,
                positions.Select(position => new DepositResponsibilityTransferPosition(
                    position.TenantId,
                    position.BankAccountId,
                    new Money(position.Amount))).ToList(),
                "Property ownership transfer",
                command.SourceRef),
            ct);
    }

    private sealed record HeldDepositPositionRow(Guid TenantId, Guid BankAccountId, decimal Amount);
}
