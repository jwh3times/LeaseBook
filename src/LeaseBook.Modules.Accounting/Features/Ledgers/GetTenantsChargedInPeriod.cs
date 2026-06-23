using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// Returns the subset of <paramref name="TenantIds"/> that already have at least one
/// <c>journal_entries</c> row with the given <paramref name="EventType"/> (and
/// <paramref name="EventSubtype"/> when provided) whose <c>entry_date</c> falls within
/// the calendar month [<paramref name="Year"/>, <paramref name="Month"/>].
/// <para>
/// Used by M6 bulk-run strategies via the <c>IPeriodChargeGuard</c> port + host adapter
/// (ADR-007) to detect rent/late-fee charges already posted for the period by ANY means
/// (manual, import, seed — not just the bulk run's own source_ref). Prevents cross-source
/// double-charging (CLAUDE.md fiduciary invariant).
/// </para>
/// <para>
/// <c>tenant_id</c> lives on <c>journal_lines</c> (not headers); the query JOINs to find
/// any line with a matching tenant dimension on a qualifying header. RLS provides org scoping.
/// </para>
/// </summary>
public sealed record GetTenantsChargedInPeriod(
    string EventType,
    string? EventSubtype,
    int Year,
    int Month,
    IReadOnlyList<Guid> TenantIds) : IQuery<TenantsChargedInPeriodResponse>;

/// <summary>The tenant ids that already have a charge of the requested type in the period.</summary>
public sealed record TenantsChargedInPeriodResponse(IReadOnlySet<Guid> TenantIds);

internal sealed class GetTenantsChargedInPeriodHandler(DbContext db)
    : IQueryHandler<GetTenantsChargedInPeriod, TenantsChargedInPeriodResponse>
{
    public async Task<TenantsChargedInPeriodResponse> Handle(
        GetTenantsChargedInPeriod query, CancellationToken ct)
    {
        if (query.TenantIds.Count == 0)
        {
            return new TenantsChargedInPeriodResponse(new HashSet<Guid>());
        }

        var tenantIds = query.TenantIds.ToArray();
        var periodFirst = new DateOnly(query.Year, query.Month, 1);
        var periodLast = new DateOnly(query.Year, query.Month,
            DateTime.DaysInMonth(query.Year, query.Month));

        IList<TenantIdRow> charged;

        if (query.EventSubtype is null)
        {
            charged = await db.Database.SqlQuery<TenantIdRow>(
                $"""
                SELECT DISTINCT jl.tenant_id
                FROM journal_entries je
                JOIN journal_lines jl ON jl.entry_id = je.id
                WHERE je.event_type = {query.EventType}
                  AND je.entry_date >= {periodFirst}
                  AND je.entry_date <= {periodLast}
                  AND jl.tenant_id = ANY({tenantIds})
                """).ToListAsync(ct);
        }
        else
        {
            var subtype = query.EventSubtype;
            charged = await db.Database.SqlQuery<TenantIdRow>(
                $"""
                SELECT DISTINCT jl.tenant_id
                FROM journal_entries je
                JOIN journal_lines jl ON jl.entry_id = je.id
                WHERE je.event_type = {query.EventType}
                  AND je.event_subtype = {subtype}
                  AND je.entry_date >= {periodFirst}
                  AND je.entry_date <= {periodLast}
                  AND jl.tenant_id = ANY({tenantIds})
                """).ToListAsync(ct);
        }

        return new TenantsChargedInPeriodResponse(
            charged.Select(r => r.TenantId).ToHashSet());
    }

    private sealed record TenantIdRow(Guid TenantId);
}
