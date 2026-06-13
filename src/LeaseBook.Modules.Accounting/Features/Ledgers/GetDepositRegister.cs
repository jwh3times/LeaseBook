using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>Held deposits and prepayments per tenant × kind (§C.6).</summary>
public sealed record GetDepositRegister : IQuery<DepositRegisterResponse>;

public sealed record DepositRegisterResponse(IReadOnlyList<DepositRegisterRow> Rows);

public sealed record DepositRegisterRow(Guid TenantId, string Kind, decimal Held);

internal sealed class GetDepositRegisterHandler(DbContext db) : IQueryHandler<GetDepositRegister, DepositRegisterResponse>
{
    public async Task<DepositRegisterResponse> Handle(GetDepositRegister query, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<DepositRegisterRow>(
            $"""
            SELECT jl.tenant_id,
                   CASE a.code WHEN 'security_deposits_held' THEN 'deposit' ELSE 'prepayment' END AS kind,
                   SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) AS held
            FROM journal_lines jl
            JOIN accounts a ON a.id = jl.account_id
            WHERE a.code IN ('security_deposits_held', 'tenant_prepayments')
              AND jl.tenant_id IS NOT NULL AND jl.basis IN ('cash', 'both')
            GROUP BY jl.tenant_id, a.code
            HAVING SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) <> 0
            ORDER BY jl.tenant_id, kind
            """).ToListAsync(ct);

        return new DepositRegisterResponse(rows);
    }
}
