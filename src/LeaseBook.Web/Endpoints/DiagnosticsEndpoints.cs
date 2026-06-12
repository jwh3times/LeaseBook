using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Endpoints;

/// <summary>
/// M0 plumbing probe (authenticated). Returns the count of audit rows visible to the caller — which
/// exercises the full chain end-to-end: auth cookie → org_id claim → org-context middleware
/// (<c>SET LOCAL app.org_id</c>) → RLS scoping. It is the WP-05 + WP-06 tie-test surface and is
/// superseded by real read endpoints from M1.
/// </summary>
public sealed class DiagnosticsEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diagnostics/audit-count", async (AppDbContext db, HttpContext http) =>
                TypedResults.Ok(new AuditCountResponse(await db.AuditEvents.CountAsync(http.RequestAborted))))
            .WithTags("Diagnostics")
            .Produces<AuditCountResponse>();
    }
}

public sealed record AuditCountResponse(int Count);
