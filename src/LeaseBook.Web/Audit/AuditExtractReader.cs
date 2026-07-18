using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Audit;

/// <summary>The compliance-pack audit extract: money-touching audit events over a period, newest-first.</summary>
public sealed record AuditExtractResponse(IReadOnlyList<AuditExtractRow> Rows);

/// <summary>One audit-trail row. <see cref="ActorName"/> is "System" for seeder/job writes.</summary>
public sealed record AuditExtractRow(
    DateTime OccurredAt, string EntityType, Guid EntityId, string Action, string ActorName, string? ActorEmail);

/// <summary>
/// Host-composed read of the money-touching audit trail for a period (WP-8). Reads <c>audit_events</c>
/// (a host table, org-scoped by RLS + the EF filter), keeps only <see cref="MoneyTouchingEntityTypes"/>
/// and the requested <c>occurred_at</c> window, and resolves each <c>actor_user_id</c> via an
/// <b>explicit org-filtered</b> <c>asp_net_users</c> lookup (the identity table carries no RLS — the org
/// filter is the isolation boundary, M3-E6). A null/unknown actor renders as "System". PMAdmin gating is
/// applied at the endpoint, not here.
/// </summary>
public sealed class AuditExtractReader(AppDbContext db, ITenantContext tenant)
{
    /// <summary>
    /// The <c>entity_type</c>s that count as money-touching for the compliance pack. <c>entity_type</c> is
    /// the snake_case table name for auto-audits, or a synthetic domain-event name for explicit audits.
    /// Kept as a single constant + drift-guarded by a test so a new money table can't silently drop out.
    /// </summary>
    public static readonly IReadOnlyList<string> MoneyTouchingEntityTypes =
    [
        "journal_entries",
        "journal_lines",
        "bank_reconciliations",
        "bank_line_status",
        "accounting_periods",
        "statement_matches",
        "statement_imports",
        "migration-signed-off",
    ];

    public async Task<AuditExtractResponse> GetAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        // occurred_at is a timestamptz wall-clock stamp; bound it to the requested day range [from, to]
        // inclusive (→ half-open [from 00:00, to+1 00:00) in UTC).
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var endExclusive = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var events = await db.AuditEvents.AsNoTracking()
            .Where(a => MoneyTouchingEntityTypes.Contains(a.EntityType)
                        && a.OccurredAt >= start && a.OccurredAt < endExclusive)
            .OrderByDescending(a => a.OccurredAt)
            .Select(a => new { a.ActorUserId, a.EntityType, a.EntityId, a.Action, a.OccurredAt })
            .ToListAsync(ct);

        var actorIds = events.Where(e => e.ActorUserId is not null)
            .Select(e => e.ActorUserId!.Value).Distinct().ToList();

        // Identity soft-spot (M3-E6): asp_net_users has no RLS, so filter by org explicitly.
        var actors = await db.Users.AsNoTracking()
            .Where(u => u.OrgId == tenant.OrgId && actorIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email })
            .ToListAsync(ct);
        var byId = actors.ToDictionary(u => u.Id);

        var rows = events
            .Select(e =>
            {
                var actor = e.ActorUserId is { } id && byId.TryGetValue(id, out var u) ? u : null;
                return new AuditExtractRow(
                    e.OccurredAt, e.EntityType, e.EntityId, e.Action,
                    actor?.DisplayName ?? actor?.Email ?? "System", actor?.Email);
            })
            .ToList();

        return new AuditExtractResponse(rows);
    }
}
