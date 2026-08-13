using FluentValidation;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// Resolves canonical rent source references to unreversed obligations that remain open under
/// oldest-charge-first receivable allocation at the assessment date.
/// </summary>
public sealed record GetRentObligationEntries(IReadOnlyList<string> SourceRefs, DateOnly AsOf)
    : IQuery<RentObligationEntriesResponse>;

public sealed record RentObligationEntriesResponse(IReadOnlyList<RentObligationEntryRow> Rows);

public sealed record RentObligationEntryRow(Guid EntryId, string SourceRef, Guid TenantId);

internal sealed class GetRentObligationEntriesValidator : AbstractValidator<GetRentObligationEntries>
{
    public GetRentObligationEntriesValidator()
    {
        RuleFor(x => x.SourceRefs).NotEmpty();
        RuleFor(x => x.AsOf).NotEmpty();
    }
}

internal sealed class GetRentObligationEntriesHandler(DbContext db)
    : IQueryHandler<GetRentObligationEntries, RentObligationEntriesResponse>
{
    public async Task<RentObligationEntriesResponse> Handle(
        GetRentObligationEntries query,
        CancellationToken ct)
    {
        var sourceRefs = query.SourceRefs.Distinct(StringComparer.Ordinal).ToArray();
        var asOf = query.AsOf;
        var rows = await db.Database.SqlQuery<RentObligationEntryRow>(
            $"""
            WITH receivable_entries AS (
                SELECT e.id AS entry_id,
                       e.source_ref,
                       e.event_type,
                       e.entry_date,
                       e.posted_at,
                       jl.tenant_id,
                       SUM(COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0)) AS net_owed
                FROM journal_entries e
                JOIN journal_lines jl ON jl.entry_id = e.id
                WHERE jl.account_class = 'tenant_receivable'
                  AND jl.basis IN ('accrual', 'both')
                  AND jl.tenant_id IS NOT NULL
                  AND e.entry_date <= {asOf}
                GROUP BY e.id, e.source_ref, e.event_type, e.entry_date, e.posted_at, jl.tenant_id
            )
            SELECT target.entry_id, target.source_ref, target.tenant_id
            FROM receivable_entries target
            WHERE target.event_type = 'RentCharged'
              AND target.source_ref = ANY({sourceRefs})
              AND NOT EXISTS (
                  SELECT 1 FROM journal_entries reversal
                  WHERE reversal.reverses_entry_id = target.entry_id)
              AND LEAST(
                  target.net_owed,
                  GREATEST(
                      COALESCE((
                          SELECT SUM(GREATEST(prior.net_owed, 0))
                          FROM receivable_entries prior
                          WHERE prior.tenant_id = target.tenant_id
                            AND (prior.entry_date, prior.posted_at, prior.entry_id)
                                <= (target.entry_date, target.posted_at, target.entry_id)
                      ), 0)
                      - COALESCE((
                          SELECT SUM(GREATEST(-activity.net_owed, 0))
                          FROM receivable_entries activity
                          WHERE activity.tenant_id = target.tenant_id
                      ), 0),
                      0)) > 0
            """).ToListAsync(ct);

        return new RentObligationEntriesResponse(rows);
    }
}
