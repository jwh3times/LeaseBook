using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Web.Endpoints;

/// <summary>
/// The per-entry audit trail endpoint (§C.3 / P56): <c>GET /api/accounting/entries/{id}/audit</c>
/// (RequirePMStaff). Host-owned because the read joins host audit/identity tables with the Accounting
/// reversal link (the composition root, like the dashboard). Thin lambda → <see cref="EntryAuditReader"/>.
/// </summary>
public sealed class AuditEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/accounting/entries/{entryId:guid}/audit",
                async (Guid entryId, EntryAuditReader reader, CancellationToken ct) =>
                    TypedResults.Ok(await reader.GetAsync(entryId, ct)))
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Accounting")
            .Produces<EntryAuditResponse>();
    }
}
