using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Audit;

/// <summary>The per-entry audit trail (§C.3 / P56): who/when/what for a journal entry and its reversal.</summary>
public sealed record EntryAuditResponse(IReadOnlyList<AuditRow> Rows);

/// <summary>One audit row, newest-first. <see cref="ActorName"/> is "System" for seeder/job writes.</summary>
public sealed record AuditRow(DateTime OccurredAt, string Action, string ActorName, string? ActorEmail);

/// <summary>
/// Host-composed read of the per-entry audit trail (P56). Reads <c>audit_events</c> (a host table,
/// org-scoped by RLS + the EF filter) for the entry <b>and any entry that reverses it</b>, then resolves
/// each <c>actor_user_id</c> to a display name/email via an <b>explicit org-filtered</b> <c>asp_net_users</c>
/// lookup — the identity soft-spot carries no RLS, so the org filter is the isolation boundary here
/// (M3-E6). A null/unknown actor renders as "System". Lives in the host because it joins host
/// (audit/identity) and Accounting (the reversal link) data — the composition root's job.
/// </summary>
public sealed class EntryAuditReader(AppDbContext db, ITenantContext tenant)
{
    public async Task<EntryAuditResponse> GetAsync(Guid entryId, CancellationToken ct)
    {
        // The entry plus any reversal of it (RLS-scoped to the caller's org via the EF filter).
        var entryIds = await db.Set<JournalEntry>().AsNoTracking()
            .Where(e => e.Id == entryId || e.ReversesEntryId == entryId)
            .Select(e => e.Id)
            .ToListAsync(ct);

        if (entryIds.Count == 0)
        {
            return new EntryAuditResponse([]);
        }

        var events = await db.AuditEvents.AsNoTracking()
            .Where(a => a.EntityType == "journal_entries" && entryIds.Contains(a.EntityId))
            .OrderByDescending(a => a.OccurredAt)
            .Select(a => new { a.ActorUserId, a.Action, a.OccurredAt })
            .ToListAsync(ct);

        var actorIds = events.Where(e => e.ActorUserId is not null)
            .Select(e => e.ActorUserId!.Value).Distinct().ToList();

        // Identity soft-spot (P56/M3-E6): asp_net_users has no RLS, so filter by org explicitly.
        var actors = await db.Users.AsNoTracking()
            .Where(u => u.OrgId == tenant.OrgId && actorIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email })
            .ToListAsync(ct);
        var byId = actors.ToDictionary(u => u.Id);

        var rows = events
            .Select(e =>
            {
                var actor = e.ActorUserId is { } id && byId.TryGetValue(id, out var u) ? u : null;
                return new AuditRow(
                    e.OccurredAt, e.Action, actor?.DisplayName ?? actor?.Email ?? "System", actor?.Email);
            })
            .ToList();

        return new EntryAuditResponse(rows);
    }
}
