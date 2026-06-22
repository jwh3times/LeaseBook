using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.BankAccounts;

/// <summary>All bank accounts for the org (§C.4), newest first. Pass <c>ActiveOnly: true</c> to restrict
/// to active accounts only (e.g. for posting pickers).</summary>
public sealed record ListBankAccounts(bool ActiveOnly = false) : IQuery<IReadOnlyList<BankAccountResponse>>;

/// <summary>One bank account by id (§C.4); null → 404 at the endpoint.</summary>
public sealed record GetBankAccount(Guid Id) : IQuery<BankAccountResponse?>;

public sealed record BankAccountResponse(
    Guid Id, string Name, string? Institution, string? Mask, string Purpose, bool IsActive)
{
    public static BankAccountResponse From(BankAccount b) =>
        new(b.Id, b.Name, b.Institution, b.Mask, BankPurposeConverter.ToDb(b.Purpose), b.IsActive);
}

internal sealed class ListBankAccountsHandler(DbContext db)
    : IQueryHandler<ListBankAccounts, IReadOnlyList<BankAccountResponse>>
{
    public async Task<IReadOnlyList<BankAccountResponse>> Handle(ListBankAccounts query, CancellationToken ct)
    {
        var banks = db.Set<BankAccount>().AsNoTracking();
        if (query.ActiveOnly)
        {
            banks = banks.Where(b => b.IsActive);
        }

        var rows = await banks.OrderByDescending(b => b.CreatedAt).ToListAsync(ct);
        return [.. rows.Select(BankAccountResponse.From)];
    }
}

internal sealed class GetBankAccountHandler(DbContext db) : IQueryHandler<GetBankAccount, BankAccountResponse?>
{
    public async Task<BankAccountResponse?> Handle(GetBankAccount query, CancellationToken ct)
    {
        var bank = await db.Set<BankAccount>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == query.Id, ct);
        return bank is null ? null : BankAccountResponse.From(bank);
    }
}
