using LeaseBook.Modules.Banking.Features.Import;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Modules.Banking.Endpoints;

/// <summary>
/// The M4 CSV statement-import surface (ADR-015 / P71): upload + dedup, saved column mappings, the match
/// preview, and confirm (which clears matched lines through the Accounting clearance port). All
/// <c>RequirePMStaff</c>, thin (bind → dispatch → <c>TypedResults</c>); validation failures flow to the
/// host's <c>ValidationExceptionHandler</c> (→ 400).
/// </summary>
public sealed class ImportEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/banking")
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Banking");

        group.MapPost("/banks/{bankAccountId:guid}/imports",
                async (Guid bankAccountId, ImportStatement body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body with { BankAccountId = bankAccountId }, ct)))
            .Produces<ImportResult>();

        group.MapGet("/banks/{bankAccountId:guid}/mappings",
                async (Guid bankAccountId, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new GetColumnMappings(bankAccountId), ct)))
            .Produces<ColumnMappingsResponse>();

        group.MapPost("/banks/{bankAccountId:guid}/mappings",
                async (Guid bankAccountId, SaveColumnMapping body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body with { BankAccountId = bankAccountId }, ct)))
            .Produces<SaveColumnMappingResult>();

        group.MapGet("/imports/{importId:guid}/matches",
                async (Guid importId, ISender sender, CancellationToken ct) =>
                {
                    var preview = await sender.Query(new GetMatchPreview(importId), ct);
                    return preview is null ? Results.NotFound() : Results.Ok(preview);
                })
            .Produces<MatchPreviewResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/imports/{importId:guid}/confirm",
                async (Guid importId, ConfirmMatches body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body with { ImportId = importId }, ct)))
            .Produces<ConfirmMatchesResult>();
    }
}
