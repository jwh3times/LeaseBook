using System.Globalization;
using System.Text.Json;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Audit;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Web.Endpoints;

/// <summary>
/// The PMAdmin audit-review surface (#321). <c>RequirePMAdmin</c> on the group, for the same reason the
/// compliance pack is admin-only: this reads the whole org's audit trail, including who changed what
/// about whom.
/// <list type="bullet">
/// <item><c>GET /api/audit/filters</c> — the filter vocabularies and this org's actors.</item>
/// <item><c>GET /api/audit/events</c> — a filtered, paged page of the trail (metadata only).</item>
/// <item><c>GET /api/audit/events/{id}</c> — one event with its payload diff.</item>
/// <item><c>GET /api/audit/events.csv</c> — the same filtered rows as a download, itself audited.</item>
/// </list>
/// <para>
/// Host-owned because the read joins the host's audit and identity tables. Thin lambdas →
/// <see cref="AuditLogReader"/>; the one piece of logic here is parsing the <c>actor</c> parameter,
/// which is what keeps "a user" and "the system" from being two filters that can contradict each other.
/// </para>
/// </summary>
public sealed class AuditLogEndpoints : IEndpointModule
{
    /// <summary>The <c>actor</c> value that selects automated writes rather than a person.</summary>
    public const string SystemActor = "system";

    /// <summary>
    /// The <c>entity_type</c> recorded when an administrator exports the trail. Taking a copy of the
    /// whole organization's audit history is at least as audit-worthy as generating a compliance pack,
    /// which has recorded itself since WP-8 — and "who pulled the audit log" is precisely the kind of
    /// question this surface exists to answer, so a surface that could not answer it about itself
    /// would be arguing against its own premise.
    /// </summary>
    public const string ExportAuditEntityType = "audit-export-generated";

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit")
            .RequireAuthorization("RequirePMAdmin")
            .WithTags("Audit");

        // GET /api/audit/filters — entity types, actions, and this org's people.
        group.MapGet("/filters",
                async (AuditLogReader reader, CancellationToken ct) =>
                    TypedResults.Ok(await reader.GetFilterOptionsAsync(ct)))
            .Produces<AuditFilterOptions>();

        // GET /api/audit/events?from=&to=&actor=&entityType=&action=&page=&pageSize=
        group.MapGet("/events",
                async (DateOnly? from, DateOnly? to, string? actor, string? entityType,
                    string? action, int? page, int? pageSize,
                    AuditLogReader reader, HttpContext httpContext, CancellationToken ct) =>
                {
                    if (!TryFilters(from, to, actor, entityType, action, page, pageSize, out var filters))
                    {
                        return InvalidActor(httpContext);
                    }

                    return Results.Ok(await reader.GetAsync(filters, ct));
                })
            .Produces<AuditLogResponse>()
            // The `actor` rejection below. Declared so it reaches the OpenAPI document and the
            // generated client: an error the endpoint can return and the published contract does not
            // mention is a contract the SPA cannot be written against.
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // GET /api/audit/events/{id} — one event plus its field-level diff.
        group.MapGet("/events/{id:guid}",
                async (Guid id, AuditLogReader reader, CancellationToken ct) =>
                {
                    var detail = await reader.GetDetailAsync(id, ct);
                    return detail is null ? Results.NotFound() : Results.Ok(detail);
                })
            .Produces<AuditEventDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // GET /api/audit/events.csv?… — the filtered rows as a download, capped by AuditLogCsv.MaxRows.
        group.MapGet("/events.csv",
                async (DateOnly? from, DateOnly? to, string? actor, string? entityType, string? action,
                    AuditLogReader reader, AppDbContext db, IActorContext actors, IOrgContext org,
                    HttpContext httpContext, CancellationToken ct) =>
                {
                    if (!TryFilters(from, to, actor, entityType, action,
                            page: 1, pageSize: AuditLogCsv.MaxRows, out var filters))
                    {
                        return InvalidActor(httpContext);
                    }

                    // The export's own ceiling, not the browse page cap — see AuditLogFilters.MaxPageSize.
                    var result = await reader.GetAsync(filters, AuditLogCsv.MaxRows, ct);
                    var bytes = AuditLogCsv.Write(result.Rows, result.Total);
                    var generatedAt = DateTime.UtcNow;

                    var acting = actors.Actor ?? throw new InvalidOperationException(
                        "Exporting the audit log requires a declared actor (ADR-039).");
                    db.Set<AuditEvent>().Add(new AuditEvent
                    {
                        Id = UuidV7.NewId(),
                        OrgId = org.OrgId ?? Guid.Empty,
                        ActorUserId = acting.UserId,
                        ActorKind = acting.Kind,
                        ActorProcess = acting.Process,
                        EntityType = ExportAuditEntityType,
                        EntityId = org.OrgId ?? Guid.Empty,
                        Action = "insert",
                        // The narrowing and the size, not the rows: the payload of an export of audit
                        // metadata would be a second copy of that metadata inside the trail.
                        After = JsonSerializer.Serialize(new
                        {
                            from,
                            to,
                            actor,
                            entityType,
                            action,
                            exported = result.Rows.Count,
                            matched = result.Total,
                            generatedAt,
                        }),
                        OccurredAt = generatedAt,
                    });
                    await db.SaveChangesAsync(ct);

                    var stamp = generatedAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    return Results.File(bytes, "text/csv", $"audit-log-{stamp}.csv");
                })
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Binds the query string to <see cref="AuditLogFilters"/>, resolving the single <c>actor</c>
    /// parameter to either a user id or the system flag. Returns <see langword="false"/> only when
    /// <c>actor</c> is present and is neither.
    /// </summary>
    private static bool TryFilters(
        DateOnly? from, DateOnly? to, string? actor, string? entityType, string? action,
        int? page, int? pageSize, out AuditLogFilters filters)
    {
        filters = new AuditLogFilters(
            From: from,
            To: to,
            EntityType: entityType,
            Action: action,
            Page: page ?? 1,
            PageSize: pageSize ?? AuditLogFilters.DefaultPageSize);

        if (string.IsNullOrWhiteSpace(actor))
        {
            return true;
        }

        var trimmed = actor.Trim();
        if (string.Equals(trimmed, SystemActor, StringComparison.OrdinalIgnoreCase))
        {
            filters = filters with { SystemActorsOnly = true };
            return true;
        }

        if (Guid.TryParse(trimmed, out var actorUserId) && actorUserId != Guid.Empty)
        {
            filters = filters with { ActorUserId = actorUserId };
            return true;
        }

        return false;
    }

    private static IResult InvalidActor(HttpContext httpContext) =>
        ProblemResults.Problem(
            httpContext,
            code: "audit_actor_invalid",
            detail: $"Filter by a person, or by '{SystemActor}' for automated writes.",
            status: StatusCodes.Status400BadRequest);
}
