using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Tenants;

public sealed record PaymentEligibilityKey(Guid TenantId, Guid BankId, DateOnly Date);
public sealed record GetPaymentEligibility(IReadOnlyList<PaymentEligibilityKey> Items)
    : IQuery<IReadOnlyDictionary<PaymentEligibilityKey, bool>>;

internal sealed class GetPaymentEligibilityHandler(DbContext db)
    : IQueryHandler<GetPaymentEligibility, IReadOnlyDictionary<PaymentEligibilityKey, bool>>
{
    public async Task<IReadOnlyDictionary<PaymentEligibilityKey, bool>> Handle(GetPaymentEligibility q, CancellationToken ct)
    {
        var ids = q.Items.Select(x => x.BankId).Distinct().ToArray();
        var banks = await db.Set<BankAccount>().Where(x => ids.Contains(x.Id) && x.IsActive && x.Purpose == BankPurpose.Trust)
            .Select(x => x.Id).ToListAsync(ct);
        var result = new Dictionary<PaymentEligibilityKey, bool>();
        var dimensions = new GetTenantPostingDimensionsHandler(db);
        foreach (var key in q.Items.Distinct())
        {
            result[key] = banks.Contains(key.BankId)
                && await dimensions.Handle(new GetTenantPostingDimensions(key.TenantId, key.Date), ct) is not null;
        }
        return result;
    }
}
