using FluentValidation;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Refunds;

/// <summary>
/// Batch read of each tenant's refundable held buckets (#473): every deposit and prepayment bucket with a
/// positive balance, with the bank that holds it. The refund-check form uses it to prefill the amount and
/// to offer a bucket choice only when there is more than one.
/// </summary>
public sealed record GetRefundableBalances(IReadOnlyCollection<Guid> TenantIds)
    : IQuery<IReadOnlyList<RefundableBalance>>;

public sealed class GetRefundableBalancesValidator : AbstractValidator<GetRefundableBalances>
{
    public GetRefundableBalancesValidator()
    {
        RuleFor(x => x.TenantIds).NotEmpty().Must(ids => ids.Count <= 500)
            .WithMessage("Request at most 500 tenants at a time.");
    }
}

internal sealed class GetRefundableBalancesHandler(DbContext db)
    : IQueryHandler<GetRefundableBalances, IReadOnlyList<RefundableBalance>>
{
    public Task<IReadOnlyList<RefundableBalance>> Handle(GetRefundableBalances query, CancellationToken ct) =>
        RefundChecks.ReadAsync(db, query.TenantIds, ct);
}
