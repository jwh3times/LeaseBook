using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / P49) for Directory's <see cref="ITenantFinancials"/> port. Delegates to the
/// Accounting read models via <see cref="ISender"/> — the cross-module reference lives here in the host,
/// never in Directory. Returns batch maps the consumer merges in memory (M2-E12). DI-scoped, so the
/// dispatched query rides the request's ambient org transaction (RLS applies).
/// </summary>
internal sealed class TenantFinancialsAdapter(ISender sender) : ITenantFinancials
{
    public async Task<IReadOnlyDictionary<Guid, decimal>> BalancesAsync(CancellationToken ct)
    {
        var response = await sender.Query(new GetTenantBalances(), ct);
        return response.Rows.ToDictionary(r => r.TenantId, r => r.Balance);
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> DepositsHeldAsync(CancellationToken ct)
    {
        var register = await sender.Query(new GetDepositRegister(), ct);
        return register.Rows
            .Where(r => r.Kind == "deposit")
            .ToDictionary(r => r.TenantId, r => r.Held);
    }

    public async Task<IReadOnlyDictionary<Guid, TenantFinancialStanding>> StandingAsync(
        DateOnly asOf,
        CancellationToken ct)
    {
        var aging = await sender.Query(new GetDelinquencyAging(asOf), ct);
        var held = await sender.Query(new GetDepositRegister(AsOf: asOf), ct);

        var result = aging.Rows.ToDictionary(
            row => row.TenantId,
            row => new TenantFinancialStanding(
                row.D1_30 + row.D31_60 + row.D61_90 + row.Over90,
                row.UnappliedCredit));

        foreach (var prepayment in held.Rows.Where(row => row.Kind == "prepayment"))
        {
            var current = result.GetValueOrDefault(prepayment.TenantId, new TenantFinancialStanding(0m, 0m));
            result[prepayment.TenantId] = current with
            {
                UnappliedCredit = current.UnappliedCredit + prepayment.Held,
            };
        }

        return result;
    }
}
