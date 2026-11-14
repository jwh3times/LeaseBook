using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.BankAccounts;

/// <summary>
/// What each named bank account is used for, as a map (ADR-007 batch shape). The host's
/// <c>IBankAccountPurposes</c> adapter dispatches it for an Accounting template that must tell an
/// operating trust from a security-deposit trust, which the chart of accounts does not (#744). An id the
/// org cannot see is absent from the map. Inactive accounts are included: whether an inactive account may
/// take a posting is not this read's question.
/// </summary>
public sealed record GetBankAccountPurposes(IReadOnlyCollection<Guid> BankAccountIds)
    : IQuery<IReadOnlyDictionary<Guid, BankPurpose>>;

internal sealed class GetBankAccountPurposesHandler(DbContext db)
    : IQueryHandler<GetBankAccountPurposes, IReadOnlyDictionary<Guid, BankPurpose>>
{
    public async Task<IReadOnlyDictionary<Guid, BankPurpose>> Handle(GetBankAccountPurposes query, CancellationToken ct)
    {
        var ids = query.BankAccountIds.Distinct().ToArray();
        return await db.Set<BankAccount>().AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.Purpose, ct);
    }
}
