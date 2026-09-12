using System.Text.Json;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Audit;

/// <summary>
/// What the review surface asks for. <see cref="ActorUserId"/> and <see cref="SystemActorsOnly"/> are
/// mutually exclusive by construction — the endpoint parses one <c>actor</c> parameter into whichever
/// applies, so no request can ask for "user X, but only system writes".
/// </summary>
public sealed record AuditLogFilters(
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? ActorUserId = null,
    bool SystemActorsOnly = false,
    string? EntityType = null,
    string? Action = null,
    int Page = 1,
    int PageSize = AuditLogFilters.DefaultPageSize)
{
    public const int DefaultPageSize = 50;

    /// <summary>
    /// The most rows one <b>browse</b> request may ask for. The export asks for more, and passes its
    /// own ceiling to <see cref="Normalize"/> — clamping it to this silently would truncate the file
    /// at 200 rows while its own header claimed a ten-thousand-row limit.
    /// </summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// Clamps paging and trims blank filter strings to null, so "" never filters on empty.
    /// <paramref name="maxPageSize"/> is the caller's ceiling: the browse endpoints take the default,
    /// the export passes <c>AuditLogCsv.MaxRows</c>.
    /// </summary>
    public AuditLogFilters Normalize(int maxPageSize = MaxPageSize) => this with
    {
        EntityType = string.IsNullOrWhiteSpace(EntityType) ? null : EntityType.Trim(),
        Action = string.IsNullOrWhiteSpace(Action) ? null : Action.Trim(),
        Page = Page > 0 ? Page : 1,
        PageSize = Math.Clamp(PageSize, 1, maxPageSize),
    };

    public int Skip => (Page - 1) * PageSize;
}

/// <summary>
/// One row of the review list. Metadata only — the <c>before</c>/<c>after</c> snapshots are fetched
/// per event by <see cref="AuditLogReader.GetDetailAsync"/>, so browsing never ships a payload nobody
/// asked to read.
/// </summary>
public sealed record AuditLogRow(
    Guid Id,
    DateTime OccurredAt,
    string EntityType,
    Guid EntityId,
    string Action,
    string ActorName,
    string? ActorEmail);

/// <summary>A page of the review list, plus the unpaged total the pager needs.</summary>
public sealed record AuditLogResponse(IReadOnlyList<AuditLogRow> Rows, int Total, int Page, int PageSize);

/// <summary>
/// One field of an audit event's payload. <see cref="Redacted"/> says the value was withheld by
/// <see cref="AuditFieldRedaction"/> rather than absent — a distinction the reviewer needs, because
/// "you may not read this" and "this was cleared" are different facts.
/// </summary>
public sealed record AuditFieldChange(string Field, string? Before, string? After, bool Redacted);

/// <summary>One audit event with its payload rendered as a field-level diff.</summary>
public sealed record AuditEventDetail(AuditLogRow Event, IReadOnlyList<AuditFieldChange> Changes);

/// <summary>A person the reviewer can filter by. The system actor is not one of these — it is a fixed
/// choice the surface offers, because no row in any table represents it.</summary>
public sealed record AuditActorOption(Guid Id, string Name, string? Email);

/// <summary>What the review surface can filter by: the full vocabularies, plus this org's people.</summary>
public sealed record AuditFilterOptions(
    IReadOnlyList<string> EntityTypes,
    IReadOnlyList<string> Actions,
    IReadOnlyList<AuditActorOption> Actors);

/// <summary>
/// The PMAdmin audit-review read (#321): a filtered, paged view of <c>audit_events</c> across the whole
/// audited universe, and a per-event payload diff behind it.
/// <para>
/// Deliberately wider than <see cref="AuditExtractReader"/>, which keeps only the money-touching entity
/// types because it feeds an examiner's compliance pack for a reconciled period. A reviewer asking "who
/// changed this lease" or "who deactivated that bank account" is asking a question the extract cannot
/// answer, and the compliance pack refuses an unreconciled period besides — so review is a separate read,
/// not a parameter on the export.
/// </para>
/// <para>
/// Lives in the host for the same reason the other two audit readers do: it joins the host's audit and
/// identity tables, and <c>asp_net_users</c> carries no RLS, so every actor lookup filters on the org
/// explicitly (the identity soft-spot, M3-E6). The audit rows themselves are org-scoped by RLS plus the
/// EF filter. PMAdmin gating is applied at the endpoint, not here.
/// </para>
/// </summary>
public sealed class AuditLogReader(AppDbContext db, IOrgContext tenant)
{
    /// <summary>A page of the trail for browsing, capped at <see cref="AuditLogFilters.MaxPageSize"/>.</summary>
    public Task<AuditLogResponse> GetAsync(AuditLogFilters filters, CancellationToken ct) =>
        GetAsync(filters, AuditLogFilters.MaxPageSize, ct);

    /// <summary>
    /// As above, with the caller's own page ceiling. The export needs one: a file capped at the browse
    /// page size would be 200 rows under a header promising ten thousand.
    /// </summary>
    public async Task<AuditLogResponse> GetAsync(
        AuditLogFilters filters, int maxPageSize, CancellationToken ct)
    {
        var normalized = filters.Normalize(maxPageSize);
        var query = Filter(normalized);

        var total = await query.CountAsync(ct);

        var events = await InReviewOrder(query)
            .Skip(normalized.Skip)
            .Take(normalized.PageSize)
            .Select(a => new
            {
                a.Id,
                a.ActorUserId,
                a.ActorKind,
                a.ActorProcess,
                a.EntityType,
                a.EntityId,
                a.Action,
                a.OccurredAt,
            })
            .ToListAsync(ct);

        var actors = await ResolveActorsAsync(events.Select(e => e.ActorUserId), ct);

        var rows = events
            .Select(e => Row(
                e.Id, e.OccurredAt, e.EntityType, e.EntityId, e.Action,
                e.ActorUserId, e.ActorKind, e.ActorProcess, actors))
            .ToList();

        return new AuditLogResponse(rows, total, normalized.Page, normalized.PageSize);
    }

    /// <summary>
    /// One event with its payload diff, or <see langword="null"/> when no such event is visible to this
    /// org — RLS makes a cross-org id indistinguishable from a nonexistent one, which is the intended
    /// answer either way.
    /// </summary>
    public async Task<AuditEventDetail?> GetDetailAsync(Guid id, CancellationToken ct)
    {
        var found = await db.AuditEvents.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new
            {
                a.Id,
                a.ActorUserId,
                a.ActorKind,
                a.ActorProcess,
                a.EntityType,
                a.EntityId,
                a.Action,
                a.OccurredAt,
                a.Before,
                a.After,
            })
            .FirstOrDefaultAsync(ct);

        if (found is null)
        {
            return null;
        }

        var actors = await ResolveActorsAsync([found.ActorUserId], ct);
        var row = Row(
            found.Id, found.OccurredAt, found.EntityType, found.EntityId, found.Action,
            found.ActorUserId, found.ActorKind, found.ActorProcess, actors);

        return new AuditEventDetail(row, Diff(found.Before, found.After));
    }

    /// <summary>
    /// The filter vocabularies, for the surface's selects. Entity types and actions come from the
    /// catalogs rather than from <c>SELECT DISTINCT</c> over the audit table — see
    /// <see cref="AuditEntityTypes"/> for why. Actors are this org's people, read from the identity table
    /// under an explicit org filter (M3-E6), which is cheap and complete where a distinct scan of
    /// <c>audit_events</c> would be neither.
    /// </summary>
    public async Task<AuditFilterOptions> GetFilterOptionsAsync(CancellationToken ct)
    {
        var actors = await db.Users.AsNoTracking()
            .Where(u => u.OrgId == tenant.OrgId)
            .OrderBy(u => u.DisplayName ?? u.Email)
            .Select(u => new AuditActorOption(u.Id, u.DisplayName ?? u.Email ?? "Unknown", u.Email))
            .ToListAsync(ct);

        return new AuditFilterOptions(AuditEntityTypes.All(db.Model), AuditActions.All, actors);
    }

    /// <summary>
    /// Newest first, with <c>id</c> breaking ties. <c>occurred_at</c> leads because it is what the reviewer
    /// reads by and what the index is built on — but it is not unique: one <c>SaveChanges</c> stamps a whole
    /// change set at a single instant, so an eight-row write is eight rows at one timestamp. Ordering by
    /// that alone leaves the rest to the plan, and a pager that asks for page 1 then page 2 can then repeat
    /// or skip a row with nothing in either response to say so.
    /// <para>
    /// Pulled out as its own method because the tiebreak has <b>no observable behaviour to test</b> — drop
    /// it and Postgres still returns the same order for these queries, so an end-to-end paging assertion
    /// passes either way (it was written, run against the regression, and stayed green). What can be
    /// asserted is the SQL, which is what <c>Review_order_breaks_ties_on_id</c> does.
    /// </para>
    /// </summary>
    internal static IOrderedQueryable<AuditEvent> InReviewOrder(IQueryable<AuditEvent> query) =>
        query.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id);

    /// <summary>Every row the filters select, unordered and unpaged — the count and the page share it.</summary>
    private IQueryable<AuditEvent> Filter(AuditLogFilters f)
    {
        var query = db.AuditEvents.AsNoTracking();

        if (f.From is { } from)
        {
            var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(a => a.OccurredAt >= start);
        }

        if (f.To is { } to)
        {
            // Inclusive of the whole `to` day: [to 00:00, to+1 00:00) in UTC, as the extract bounds it.
            var endExclusive = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(a => a.OccurredAt < endExclusive);
        }

        if (f.ActorUserId is { } actorUserId)
        {
            query = query.Where(a => a.ActorUserId == actorUserId);
        }
        else if (f.SystemActorsOnly)
        {
            // actor_kind is null on rows written before ADR-039, which cannot say whether a process
            // acted. Those are not claimed as system writes here — same reading as AuditActorLabel.
            query = query.Where(a => a.ActorKind == Actor.SystemKind);
        }

        if (f.EntityType is { } entityType)
        {
            query = query.Where(a => a.EntityType == entityType);
        }

        if (f.Action is { } action)
        {
            query = query.Where(a => a.Action == action);
        }

        return query;
    }

    /// <summary>
    /// Display names for the actors in a page. The identity table carries no RLS, so the org filter is
    /// the isolation boundary here (M3-E6) — an actor id from another org resolves to nothing and renders
    /// as "System" rather than leaking a name.
    /// </summary>
    private async Task<Dictionary<Guid, ActorIdentity>> ResolveActorsAsync(
        IEnumerable<Guid?> actorUserIds, CancellationToken ct)
    {
        var ids = actorUserIds.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var users = await db.Users.AsNoTracking()
            .Where(u => u.OrgId == tenant.OrgId && ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email })
            .ToListAsync(ct);

        return users.ToDictionary(u => u.Id, u => new ActorIdentity(u.DisplayName, u.Email));
    }

    private static AuditLogRow Row(
        Guid id, DateTime occurredAt, string entityType, Guid entityId, string action,
        Guid? actorUserId, string? actorKind, string? actorProcess, Dictionary<Guid, ActorIdentity> actors)
    {
        var actor = actorUserId is { } userId && actors.TryGetValue(userId, out var found)
            ? found
            : new ActorIdentity(null, null);

        return new AuditLogRow(
            id, occurredAt, entityType, entityId, action,
            AuditActorLabel.For(actor.DisplayName, actor.Email, actorProcess, actorKind), actor.Email);
    }

    /// <summary>
    /// The payload rendered field by field. An insert reports only <c>after</c>, a delete only
    /// <c>before</c>, and an update reports the fields that actually differ — an unchanged column in a
    /// twenty-column snapshot is noise that hides the one that moved.
    /// </summary>
    internal static IReadOnlyList<AuditFieldChange> Diff(string? before, string? after)
    {
        if (before is null && after is null)
        {
            return [];
        }

        var beforeValues = Parse(before);
        var afterValues = Parse(after);
        if (beforeValues is null || afterValues is null)
        {
            // A payload this reader cannot read field by field is withheld rather than dumped raw:
            // whatever it is, it is not the flat column snapshot the drawer is built to explain.
            return
            [
                new AuditFieldChange(
                    UnreadablePayloadField,
                    before is null ? null : AuditFieldRedaction.Marker,
                    after is null ? null : AuditFieldRedaction.Marker,
                    Redacted: true),
            ];
        }

        var isUpdate = before is not null && after is not null;
        var changes = new List<AuditFieldChange>();
        var fields = beforeValues.Keys
            .Concat(afterValues.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(field => field, StringComparer.Ordinal);

        foreach (var field in fields)
        {
            var beforeValue = beforeValues.GetValueOrDefault(field);
            var afterValue = afterValues.GetValueOrDefault(field);

            // Compared before redaction, so a withheld field still collapses when it did not move.
            if (isUpdate && beforeValue == afterValue)
            {
                continue;
            }

            if (AuditFieldRedaction.IsRedacted(field))
            {
                changes.Add(new AuditFieldChange(
                    field,
                    beforeValue is null ? null : AuditFieldRedaction.Marker,
                    afterValue is null ? null : AuditFieldRedaction.Marker,
                    Redacted: true));
                continue;
            }

            changes.Add(new AuditFieldChange(field, beforeValue, afterValue, Redacted: false));
        }

        return changes;
    }

    /// <summary>The field name a payload gets when it is not a flat object this reader can explain.</summary>
    internal const string UnreadablePayloadField = "(payload)";

    /// <summary>
    /// A column snapshot as field → rendered value, or <see langword="null"/> when the payload is not a
    /// flat JSON object. A <see langword="null"/> input is an absent side (an insert has no before), which
    /// reads as an empty snapshot rather than an unreadable one.
    /// </summary>
    private static Dictionary<string, string?>? Parse(string? json)
    {
        if (json is null)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = Render(property.Value);
            }

            return values;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A scalar as the reviewer should read it; anything structured keeps its JSON text — except a
    /// <c>Money</c>, which is a one-field struct and would otherwise render as <c>{"Amount":1200.00}</c>
    /// in a column a person is reading figures out of.
    /// </summary>
    private static string? Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Object when MoneyAmount(value) is { } amount => amount,
        _ => value.GetRawText(),
    };

    /// <summary>The amount inside a serialized <c>Money</c>, or null when this is some other object.</summary>
    private static string? MoneyAmount(JsonElement value)
    {
        var properties = value.EnumerateObject().ToList();
        return properties is [{ Name: "Amount", Value.ValueKind: JsonValueKind.Number } only]
            ? only.Value.GetRawText()
            : null;
    }

    private readonly record struct ActorIdentity(string? DisplayName, string? Email);
}
