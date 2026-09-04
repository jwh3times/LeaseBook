using LeaseBook.Migrator;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Web.Onboarding;

/// <summary>
/// Entity-import endpoints for the M7 onboarding wizard (WP-3 Task 3.1).
///
/// <c>POST /api/onboarding/import/{kind}</c> — uploads a CSV (as JSON body with
/// <c>csvContent</c> string field, mirroring the Banking statement import pattern), parses it
/// against the AppFolio default profile, creates
/// the corresponding Directory rows, and returns an <see cref="ImportBatchResult"/> with per-row
/// error detail. One bad row never 500s the batch; import order owners → properties → units →
/// tenants_leases is enforced by the parent FK resolution.
/// </summary>
public sealed class ImportEntitiesEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/onboarding")
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Onboarding");

        group.MapPost("/import/{kind}",
                async (
                    string kind,
                    EntityImportRequest body,
                    EntityImportService service,
                    HttpContext httpContext,
                    CancellationToken ct) =>
                {
                    if (!AppFolioImportCatalog.TryResolve(
                            kind,
                            AppFolioImportFamily.Entity,
                            out var definition))
                        return ProblemResults.Problem(
                            httpContext,
                            code: "invalid_entity_kind",
                            detail: "That is not an entity type this import supports.",
                            status: StatusCodes.Status400BadRequest);

                    // Only the documented appfolio-default profile exists today (null/empty = default).
                    // Reject any other value rather than silently parsing against the default.
                    var requested = body.MappingProfile;
                    if (!string.IsNullOrWhiteSpace(requested) && requested != definition.ProfileId)
                        return ProblemResults.Problem(
                            httpContext,
                            code: "unknown_mapping_profile",
                            detail: "That column-mapping profile is not available.",
                            status: StatusCodes.Status400BadRequest);

                    var csvBytes = System.Text.Encoding.UTF8.GetBytes(body.CsvContent ?? string.Empty);
                    await using var csvStream = new MemoryStream(csvBytes);

                    var result = await service.ImportAsync(
                        definition,
                        body.Filename ?? $"{definition.CanonicalToken}.csv",
                        csvStream,
                        ct);

                    return TypedResults.Ok(result);
                })
            .Produces<ImportBatchResult>()
            .Produces(StatusCodes.Status400BadRequest);
    }

}

/// <summary>Request body for <c>POST /api/onboarding/import/{kind}</c>.</summary>
public sealed record EntityImportRequest(
    /// <summary>CSV file content as UTF-8 text (mirrors the Banking statement-import pattern).</summary>
    string? CsvContent,
    /// <summary>Original filename (for display / audit).</summary>
    string? Filename,
    /// <summary>Optional mapping profile identifier; defaults to <c>appfolio-default</c>.</summary>
    string? MappingProfile);
