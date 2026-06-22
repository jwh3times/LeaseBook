using LeaseBook.Modules.Reporting.Catalog;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// M5 reporting endpoints (§M5 / ADR-016). Host-owned because the statement assembler and
/// preview service cross module boundaries via <see cref="LeaseBook.SharedKernel.Cqrs.ISender"/>
/// (the legitimate composition root). All <c>RequirePMStaff</c>, thin (bind → dispatch →
/// <c>TypedResults</c>).
/// <list type="bullet">
/// <item><c>GET /api/reports</c> — full catalog.</item>
/// <item><c>GET /api/reports/{id}/preview</c> — run/preview → generic rows.</item>
/// <item><c>GET /api/statements/{ownerId}</c> — assembled <see cref="StatementView"/> JSON.</item>
/// </list>
/// </summary>
public sealed class ReportingEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api")
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Reporting");

        // GET /api/reports — return the full static catalog.
        group.MapGet("/reports", () =>
                TypedResults.Ok(ReportCatalog.All))
            .Produces<IReadOnlyList<ReportDescriptor>>();

        // GET /api/reports/{id}/preview?year=&month=&ownerId=&propertyId=&bankAccountId=&asOf=
        group.MapGet("/reports/{id}/preview",
                async (string id, int? year, int? month, Guid? ownerId, Guid? propertyId,
                    Guid? bankAccountId, DateOnly? asOf,
                    ReportPreviewService previewService, CancellationToken ct) =>
                {
                    var filters = new ReportFilters(year, month, ownerId, propertyId, bankAccountId, asOf);
                    var result = await previewService.PreviewAsync(id, filters, ct);
                    return result is null
                        ? Results.NotFound(new { error = $"Report '{id}' not found in catalog." })
                        : Results.Ok(result);
                });

        // GET /api/statements/{ownerId}?propertyId=&year=&month=&basis=
        // Always returns 200 with zero figures for an owner with no journal activity in the period
        // (the owner is valid, just quiet). The fiduciary panel's Balanced flag confirms correctness.
        group.MapGet("/statements/{ownerId:guid}",
                async (Guid ownerId, Guid? propertyId, int? year, int? month, string? basis,
                    StatementAssembler assembler, CancellationToken ct) =>
                {
                    var now = DateTime.UtcNow;
                    var resolvedYear = year ?? now.Year;
                    var resolvedMonth = month ?? now.Month;
                    var resolvedBasis = basis?.ToLowerInvariant() is "accrual" ? "accrual" : "cash";

                    var views = await assembler.BuildAsync(
                        [ownerId], propertyId, resolvedYear, resolvedMonth, resolvedBasis, ct);

                    // BuildAsync returns one view per owner. An owner with no journal activity
                    // produces a zeroed statement — never an empty list for a single ownerId.
                    return TypedResults.Ok(views[0]);
                });
    }
}
