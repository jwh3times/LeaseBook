using FluentValidation;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Statements;

/// <summary>
/// The owner-equity footprint of specific journal entries: which owner, property and basis each entry
/// moved, dated when and posted when. It is what an owner statement can see of an entry, so it is what
/// decides whether an already-issued statement will carry that entry forward (ADR-045, #377).
/// </summary>
public sealed record GetOwnerEquityLines(IReadOnlyList<Guid> EntryIds) : IQuery<IReadOnlyList<OwnerEquityLine>>;

public sealed class GetOwnerEquityLinesValidator : AbstractValidator<GetOwnerEquityLines>
{
    /// <summary>The most entries one read may ask about — a run's postings, never an unbounded scan.</summary>
    public const int MaxEntryIds = 1000;

    public GetOwnerEquityLinesValidator()
    {
        RuleFor(q => q.EntryIds).NotEmpty();
        RuleFor(q => q.EntryIds.Count).LessThanOrEqualTo(MaxEntryIds);
    }
}

/// <param name="Basis"><c>cash</c>, <c>accrual</c> or <c>both</c>, as tagged on the journal line.</param>
/// <param name="Amount">Net owner-equity movement (credit − debit) for this owner, property and basis.</param>
public sealed record OwnerEquityLine(
    Guid EntryId, Guid OwnerId, Guid? PropertyId, string Basis, DateOnly EntryDate, DateTime PostedAt, decimal Amount);

internal sealed class GetOwnerEquityLinesHandler(DbContext db)
    : IQueryHandler<GetOwnerEquityLines, IReadOnlyList<OwnerEquityLine>>
{
    public async Task<IReadOnlyList<OwnerEquityLine>> Handle(GetOwnerEquityLines q, CancellationToken ct)
    {
        var ids = q.EntryIds.Distinct().ToArray();

        // One row per (entry, owner, property, basis), deliberately NOT netted to non-zero here: whether
        // lines cancel depends on the statement reading them — a cash statement sums cash + both, a
        // whole-owner statement sums every property — so the netting belongs to the matcher, per
        // candidate statement. Same owner_equity + owner_id scoping as GetOwnerStatementData, so PM
        // income is unreachable.
        var rows = await db.Database.SqlQuery<OwnerEquityLine>(
            $"""
            SELECT jl.entry_id, jl.owner_id, jl.property_id, jl.basis, e.entry_date, e.posted_at,
                   SUM(COALESCE(jl.credit,0) - COALESCE(jl.debit,0)) AS amount
            FROM journal_lines jl
            JOIN journal_entries e ON e.id = jl.entry_id
            WHERE jl.entry_id = ANY({ids})
              AND jl.account_class = 'owner_equity'
              AND jl.owner_id IS NOT NULL
            GROUP BY jl.entry_id, jl.owner_id, jl.property_id, jl.basis, e.entry_date, e.posted_at
            ORDER BY e.entry_date, e.posted_at, jl.entry_id
            """).ToListAsync(ct);

        return rows;
    }
}
