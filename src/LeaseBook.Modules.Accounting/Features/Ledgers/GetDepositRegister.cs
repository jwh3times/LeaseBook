using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// Held deposits and prepayments per tenant × kind (§C.6). Optionally scoped to one trust
/// <see cref="BankAccountId"/> and bounded to entries dated on or before <see cref="AsOf"/> (inclusive),
/// so it reads as of a period end and reconciles to that bank's held components in
/// <see cref="GetTrustEquation"/>; both null ⇒ all banks, as-of-now (WP-8).
/// </summary>
public sealed record GetDepositRegister(Guid? BankAccountId = null, DateOnly? AsOf = null)
    : IQuery<DepositRegisterResponse>;

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
            JOIN journal_entries e ON e.id = jl.entry_id
            WHERE a.code IN ('security_deposits_held', 'tenant_prepayments')
              AND jl.tenant_id IS NOT NULL AND jl.basis IN ('cash', 'both')
              AND ({query.BankAccountId}::uuid IS NULL OR jl.bank_account_id = {query.BankAccountId})
              AND ({query.AsOf}::date IS NULL OR e.entry_date <= {query.AsOf})
            GROUP BY jl.tenant_id, a.code
            HAVING SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) <> 0
            ORDER BY jl.tenant_id, kind
            """).ToListAsync(ct);

        return new DepositRegisterResponse(rows);
    }
}
