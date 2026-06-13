using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// All-tenant net balances (receivable − unapplied prepayment) in one batch read — the same figure
/// <see cref="GetTenantLedger"/> ends on, grouped per tenant. Added so a consumer (the Directory tenant
/// list, via its <c>ITenantFinancials</c> port + host adapter, P49) gets every tenant's balance in one
/// round-trip instead of N per-id ledger calls (M2-E12). Reads Accounting's own <c>journal_lines</c>, so
/// the M-E2 basis rule lives here, not in the consumer.
/// </summary>
public sealed record GetTenantBalances : IQuery<TenantBalancesResponse>;

public sealed record TenantBalancesResponse(IReadOnlyList<TenantBalanceRow> Rows);

public sealed record TenantBalanceRow(Guid TenantId, decimal Balance);

internal sealed class GetTenantBalancesHandler(DbContext db) : IQueryHandler<GetTenantBalances, TenantBalancesResponse>
{
    public async Task<TenantBalancesResponse> Handle(GetTenantBalances query, CancellationToken ct)
    {
        // balance = Σ(debit − credit) over receivable + prepayment lines (accrual/both) — receivable
        // owed minus unapplied prepayment. Security deposits are a separate register, excluded.
        var rows = await db.Database.SqlQuery<TenantBalanceRow>(
            $"""
            SELECT jl.tenant_id,
                   SUM(COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0)) AS balance
            FROM journal_lines jl
            JOIN accounts a ON a.id = jl.account_id
            WHERE jl.tenant_id IS NOT NULL
              AND a.code IN ('tenant_receivable', 'tenant_prepayments')
              AND jl.basis IN ('accrual', 'both')
            GROUP BY jl.tenant_id
            """).ToListAsync(ct);

        return new TenantBalancesResponse(rows);
    }
}
