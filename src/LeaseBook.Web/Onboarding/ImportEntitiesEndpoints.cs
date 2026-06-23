using LeaseBook.Migrator.Model;
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
/// against the AppFolio default profile (or an explicit <c>mappingProfile</c> override), creates
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
                    CancellationToken ct) =>
                {
                    if (!TryParseEntityKind(kind, out var entityKind))
                        return Results.Problem(
                            title: "Invalid entity kind",
                            detail: $"'{kind}' is not a valid entity kind for import. " +
                                    "Use one of: owners, properties, units, tenants_leases.",
                            statusCode: StatusCodes.Status400BadRequest);

                    var csvBytes = System.Text.Encoding.UTF8.GetBytes(body.CsvContent ?? string.Empty);
                    await using var csvStream = new MemoryStream(csvBytes);

                    var result = await service.ImportAsync(
                        entityKind,
                        body.MappingProfile ?? "appfolio-default",
                        body.Filename ?? $"{kind}.csv",
                        csvStream,
                        ct);

                    return TypedResults.Ok(result);
                })
            .Produces<ImportBatchResult>()
            .Produces(StatusCodes.Status400BadRequest);
    }

    private static bool TryParseEntityKind(string raw, out EntityKind kind)
    {
        kind = raw switch
        {
            "owners" => EntityKind.Owners,
            "properties" => EntityKind.Properties,
            "units" => EntityKind.Units,
            "tenants_leases" => EntityKind.TenantsLeases,
            _ => (EntityKind)(-1),
        };
        return (int)kind >= 0;
    }
}

/// <summary>Request body for <c>POST /api/onboarding/import/{kind}</c>.</summary>
public sealed record EntityImportRequest(
    /// <summary>CSV file content as UTF-8 text (mirrors the Banking statement-import pattern).</summary>
    string? CsvContent,
    /// <summary>Original filename (for display / audit).</summary>
    string? Filename,
    /// <summary>Optional mapping profile override; defaults to <c>appfolio-default</c>.</summary>
    string? MappingProfile);
