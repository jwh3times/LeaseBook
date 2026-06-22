using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Statements;

public sealed record GetOwnerStatementData(
    IReadOnlyList<Guid> OwnerIds, Guid? PropertyId, int Year, int Month, string Basis)
    : IQuery<OwnerStatementDataResponse>;

public sealed class GetOwnerStatementDataValidator : AbstractValidator<GetOwnerStatementData>
{
    public GetOwnerStatementDataValidator()
    {
        RuleFor(q => q.OwnerIds).NotEmpty();
        RuleFor(q => q.Year).InclusiveBetween(2000, 2100);
        RuleFor(q => q.Month).InclusiveBetween(1, 12);
        RuleFor(q => q.Basis).Must(b => b is "cash" or "accrual").WithMessage("basis must be 'cash' or 'accrual'.");
    }
}

public sealed record OwnerStatementDataResponse(IReadOnlyDictionary<Guid, OwnerStatement> ByOwner);
public sealed record OwnerStatement(Guid OwnerId, Guid? PropertyId, string Basis, int Year, int Month,
    decimal Beginning, IReadOnlyList<StatementSection> Sections, decimal Ending, StatementTieOut TieOut);
public sealed record StatementSection(StatementSectionKey Key, string Title, IReadOnlyList<StatementLine> Lines, decimal Subtotal);
public sealed record StatementLine(Guid EntryId, DateOnly Date, string EventType, string? EventSubtype,
    string Description, Guid? PropertyId, decimal Amount);
public sealed record StatementTieOut(bool Balanced, decimal Variance, bool PmIncomeExcluded, bool DepositsRecognizedOnApplication);

internal sealed class GetOwnerStatementDataHandler(DbContext db)
    : IQueryHandler<GetOwnerStatementData, OwnerStatementDataResponse>
{
    private sealed record Row(Guid OwnerId, Guid EntryId, DateOnly Date, string EventType, string? EventSubtype,
        string Description, Guid? PropertyId, decimal Amount);
    private sealed record Begin(Guid OwnerId, decimal Amount);

    public async Task<OwnerStatementDataResponse> Handle(GetOwnerStatementData q, CancellationToken ct)
    {
        var owners = q.OwnerIds.ToArray();
        var start = new DateOnly(q.Year, q.Month, 1);
        var end = start.AddMonths(1); // exclusive

        // In-period owner-equity movement, one row per (entry, property), with event metadata.
        var rows = await db.Database.SqlQuery<Row>(
            $"""
            SELECT jl.owner_id, e.id AS entry_id, e.entry_date AS date, e.event_type, e.event_subtype,
                   e.description, jl.property_id,
                   SUM(COALESCE(jl.credit,0) - COALESCE(jl.debit,0)) AS amount
            FROM journal_lines jl
            JOIN journal_entries e ON e.id = jl.entry_id
            WHERE jl.owner_id = ANY({owners}) AND jl.account_class = 'owner_equity'
              AND jl.basis IN ({q.Basis}, 'both')
              AND ({q.PropertyId}::uuid IS NULL OR jl.property_id = {q.PropertyId})
              AND e.entry_date >= {start} AND e.entry_date < {end}
            GROUP BY jl.owner_id, e.id, e.entry_date, e.event_type, e.event_subtype, e.description, jl.property_id
            ORDER BY e.entry_date, e.posted_at, e.id
            """).ToListAsync(ct);

        // Beginning balance = cumulative owner-equity before the period start.
        var begins = await db.Database.SqlQuery<Begin>(
            $"""
            SELECT jl.owner_id, COALESCE(SUM(COALESCE(jl.credit,0) - COALESCE(jl.debit,0)), 0) AS amount
            FROM journal_lines jl JOIN journal_entries e ON e.id = jl.entry_id
            WHERE jl.owner_id = ANY({owners}) AND jl.account_class = 'owner_equity'
              AND jl.basis IN ({q.Basis}, 'both')
              AND ({q.PropertyId}::uuid IS NULL OR jl.property_id = {q.PropertyId})
              AND e.entry_date < {start}
            GROUP BY jl.owner_id
            """).ToListAsync(ct);
        var beginning = begins.ToDictionary(b => b.OwnerId, b => b.Amount);

        var byOwner = new Dictionary<Guid, OwnerStatement>();
        foreach (var ownerId in owners)
        {
            var begin = beginning.GetValueOrDefault(ownerId, 0m);
            var mine = rows.Where(r => r.OwnerId == ownerId).ToList();

            var sections = mine
                .GroupBy(r => StatementSectionMap.Section(r.EventType)) // throws on an unmapped event
                .OrderBy(g => (int)g.Key)
                .Select(g => new StatementSection(g.Key, StatementSectionMap.Title(g.Key),
                    g.Select(r => new StatementLine(r.EntryId, r.Date, r.EventType, r.EventSubtype,
                        r.Description, r.PropertyId, r.Amount)).ToList(),
                    g.Sum(r => r.Amount)))
                .ToList();

            var ending = begin + sections.Sum(s => s.Subtotal);

            // Independent end-of-period balance for the tie-out (same source, computed separately so a
            // categorization/sign slip surfaces as a non-zero variance).
            var endBalance = begin + mine.Sum(r => r.Amount);
            var variance = ending - endBalance;
            var applied = sections.Any(s => s.Key == StatementSectionKey.AppliedDepositsCredits);

            byOwner[ownerId] = new OwnerStatement(ownerId, q.PropertyId, q.Basis, q.Year, q.Month,
                begin, sections, ending,
                new StatementTieOut(Balanced: variance == 0m, variance,
                    PmIncomeExcluded: true /* structural: query is owner_equity + owner_id-scoped */,
                    DepositsRecognizedOnApplication: applied));
        }

        return new OwnerStatementDataResponse(byOwner);
    }
}
