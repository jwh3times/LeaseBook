using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// Returns the subset of the given <see cref="CandidateKeys"/> that already exist as
/// <c>source_ref</c> values in <c>journal_entries</c> for the current org (RLS-scoped).
/// <para>
/// Used by the M6 Operations run-engine via the <c>IPostedSourceRefs</c> port + host adapter
/// to let the rent-run preview flag already-posted leases (<c>AlreadyDone</c>). This is
/// Accounting reading its own table — ADR-007 compliant (the port crosses no boundary here;
/// the boundary is the host adapter consuming this query via <see cref="ISender"/>).
/// </para>
/// </summary>
public sealed record GetExistingSourceRefs(IReadOnlyList<string> CandidateKeys)
    : IQuery<ExistingSourceRefsResponse>;

/// <summary>The subset of candidate keys that already exist as source_refs.</summary>
public sealed record ExistingSourceRefsResponse(IReadOnlySet<string> ExistingKeys);

internal sealed class GetExistingSourceRefsHandler(DbContext db)
    : IQueryHandler<GetExistingSourceRefs, ExistingSourceRefsResponse>
{
    public async Task<ExistingSourceRefsResponse> Handle(
        GetExistingSourceRefs query, CancellationToken ct)
    {
        if (query.CandidateKeys.Count == 0)
        {
            return new ExistingSourceRefsResponse(new HashSet<string>());
        }

        var keys = query.CandidateKeys.ToArray();

        // Reads only journal_entries.source_ref — Accounting's own table. RLS provides org scoping.
        var existing = await db.Database.SqlQuery<SourceRefRow>(
            $"""
            SELECT source_ref
            FROM journal_entries
            WHERE source_ref = ANY({keys})
              AND source_ref IS NOT NULL
            """).ToListAsync(ct);

        return new ExistingSourceRefsResponse(
            existing.Select(r => r.SourceRef).ToHashSet());
    }

    private sealed record SourceRefRow(string SourceRef);
}
