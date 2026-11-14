using LeaseBook.Modules.Accounting.Contracts;
using Microsoft.EntityFrameworkCore;
using DirectoryBankAccount = LeaseBook.Modules.Directory.Domain.BankAccount;
using DirectoryBankPurpose = LeaseBook.Modules.Directory.Domain.BankPurpose;

namespace LeaseBook.Tests.Common;

/// <summary>
/// The stand-in a module harness gives Accounting's <see cref="IBankAccountPurposes"/> port in place of
/// the host adapter, which dispatches a Directory query a module harness does not register. It reads
/// the same rows through the scope's own context, on its org transaction, so the purpose a template sees
/// is the one the harness seeded.
/// </summary>
public sealed class DirectoryBankPurposes(DbContext db) : IBankAccountPurposes
{
    public async Task<IReadOnlyDictionary<Guid, BankPurpose>> GetAsync(
        IReadOnlyCollection<Guid> bankAccountIds, CancellationToken ct)
    {
        var rows = await db.Set<DirectoryBankAccount>().AsNoTracking()
            .Where(b => bankAccountIds.Contains(b.Id))
            .Select(b => new { b.Id, b.Purpose })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => r.Purpose switch
        {
            DirectoryBankPurpose.Trust => BankPurpose.Trust,
            DirectoryBankPurpose.Deposit => BankPurpose.Deposit,
            DirectoryBankPurpose.Operating => BankPurpose.Operating,
            _ => throw new ArgumentOutOfRangeException(nameof(bankAccountIds), r.Purpose, null),
        });
    }
}
