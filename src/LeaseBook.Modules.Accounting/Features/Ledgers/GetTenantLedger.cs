using FluentValidation;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>A tenant's rent ledger: receivable + prepayment activity, with running net balance (§C.6).</summary>
public sealed record GetTenantLedger(Guid TenantId) : IQuery<TenantLedgerResponse>;

public sealed class GetTenantLedgerValidator : AbstractValidator<GetTenantLedger>
{
    public GetTenantLedgerValidator() => RuleFor(q => q.TenantId).NotEmpty();
}

public sealed record TenantLedgerResponse(Guid TenantId, decimal Balance, IReadOnlyList<TenantLedgerEntry> Rows);

public sealed record TenantLedgerEntry(
    Guid EntryId, DateOnly Date, string EventType, string? EventSubtype, string Category,
    string? Description, decimal Charge, decimal Payment, decimal Balance, bool IsVoided, Guid? ReversesEntryId);

internal sealed class GetTenantLedgerHandler(DbContext db) : IQueryHandler<GetTenantLedger, TenantLedgerResponse>
{
    public async Task<TenantLedgerResponse> Handle(GetTenantLedger query, CancellationToken ct)
    {
        // Rows are the tenant's receivable + prepayment lines (security deposits are a separate
        // register, excluded). charge = debits, payment = credits; the running balance =
        // receivable − unapplied prepayments (charges and payments unify because prepayment debits/
        // credits net into the same column). Ordering pins running balances (M-E5).
        var rows = await db.Database.SqlQuery<TenantLedgerSqlRow>(
            $"""
            WITH tenant_lines AS (
                SELECT jl.entry_id,
                       SUM(COALESCE(jl.debit, 0)) AS charge,
                       SUM(COALESCE(jl.credit, 0)) AS payment
                FROM journal_lines jl
                JOIN accounts a ON a.id = jl.account_id
                WHERE jl.tenant_id = {query.TenantId}
                  AND a.code IN ('tenant_receivable', 'tenant_prepayments')
                  AND jl.basis IN ('accrual', 'both')
                GROUP BY jl.entry_id
            )
            SELECT e.id AS entry_id,
                   e.entry_date AS date,
                   e.event_type,
                   e.event_subtype,
                   e.description,
                   tl.charge,
                   tl.payment,
                   SUM(tl.charge - tl.payment) OVER (
                       ORDER BY e.entry_date, e.posted_at, e.id ROWS UNBOUNDED PRECEDING) AS balance,
                   EXISTS (SELECT 1 FROM journal_entries r WHERE r.reverses_entry_id = e.id) AS is_voided,
                   e.reverses_entry_id
            FROM tenant_lines tl
            JOIN journal_entries e ON e.id = tl.entry_id
            ORDER BY e.entry_date, e.posted_at, e.id
            """).ToListAsync(ct);

        var entries = rows
            .Select(r => new TenantLedgerEntry(
                r.EntryId, r.Date, r.EventType, r.EventSubtype, Category(r.EventType, r.EventSubtype),
                r.Description, r.Charge, r.Payment, r.Balance, r.IsVoided, r.ReversesEntryId))
            .ToList();

        var balance = entries.Count > 0 ? entries[^1].Balance : 0m;
        return new TenantLedgerResponse(query.TenantId, balance, entries);
    }

    // §C.6 category derivation.
    private static string Category(string eventType, string? subtype) => eventType switch
    {
        "RentCharged" => "Rent",
        "FeeCharged" when subtype == "late" => "Late Fee",
        "FeeCharged" when subtype == "maintenance-recharge" => "Maintenance",
        "FeeCharged" => "Fee",
        "CreditIssued" => "Credit",
        "PaymentReceived" => "Payment",
        "DepositCollected" => "Security Deposit",
        "PrepaymentReceived" => "Prepayment",
        _ => eventType,
    };

    private sealed record TenantLedgerSqlRow(
        Guid EntryId, DateOnly Date, string EventType, string? EventSubtype, string? Description,
        decimal Charge, decimal Payment, decimal Balance, bool IsVoided, Guid? ReversesEntryId);
}
