using FluentValidation;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>An owner's equity ledger, per property × entry, in the requested basis (§C.6).</summary>
public sealed record GetOwnerLedger(Guid OwnerId, string Basis) : IQuery<OwnerLedgerResponse>;

public sealed class GetOwnerLedgerValidator : AbstractValidator<GetOwnerLedger>
{
    public GetOwnerLedgerValidator()
    {
        RuleFor(q => q.OwnerId).NotEmpty();
        RuleFor(q => q.Basis).Must(b => b is "cash" or "accrual")
            .WithMessage("Basis must be 'cash' or 'accrual'.");
    }
}

public sealed record OwnerLedgerResponse(Guid OwnerId, string Basis, decimal Balance, IReadOnlyList<OwnerLedgerRow> Rows);

public sealed record OwnerLedgerRow(
    Guid EntryId, DateOnly Date, string EventType, string? EventSubtype, Guid? PropertyId,
    decimal Amount, decimal Balance, bool IsVoided, Guid? ReversesEntryId);

internal sealed class GetOwnerLedgerHandler(DbContext db) : IQueryHandler<GetOwnerLedger, OwnerLedgerResponse>
{
    public async Task<OwnerLedgerResponse> Handle(GetOwnerLedger query, CancellationToken ct)
    {
        // Equity change per (entry, property) in the requested basis + both; running balance over the
        // whole owner, ordered stably.
        var rows = await db.Database.SqlQuery<OwnerLedgerRow>(
            $"""
            WITH owner_lines AS (
                SELECT jl.entry_id, jl.property_id,
                       SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) AS amount
                FROM journal_lines jl
                WHERE jl.owner_id = {query.OwnerId} AND jl.account_class = 'owner_equity'
                  AND jl.basis IN ({query.Basis}, 'both')
                GROUP BY jl.entry_id, jl.property_id
            )
            SELECT e.id AS entry_id,
                   e.entry_date AS date,
                   e.event_type,
                   e.event_subtype,
                   ol.property_id,
                   ol.amount,
                   SUM(ol.amount) OVER (
                       ORDER BY e.entry_date, e.posted_at, e.id, ol.property_id ROWS UNBOUNDED PRECEDING) AS balance,
                   EXISTS (SELECT 1 FROM journal_entries r WHERE r.reverses_entry_id = e.id) AS is_voided,
                   e.reverses_entry_id
            FROM owner_lines ol
            JOIN journal_entries e ON e.id = ol.entry_id
            ORDER BY e.entry_date, e.posted_at, e.id, ol.property_id
            """).ToListAsync(ct);

        var balance = rows.Count > 0 ? rows[^1].Balance : 0m;
        return new OwnerLedgerResponse(query.OwnerId, query.Basis, balance, rows);
    }
}
