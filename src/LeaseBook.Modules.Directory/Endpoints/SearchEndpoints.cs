using LeaseBook.Modules.Directory.Features.Search;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Modules.Directory.Endpoints;

/// <summary>
/// The ⌘K search endpoint (§C.5): <c>GET /api/search?q=&amp;limit=</c> → a ranked typed union across the
/// directory entities. Staff-level; thin lambda dispatching the <see cref="Search"/> query. Empty/oversize
/// <c>q</c> → 400 via the CQRS validation pipeline.
/// </summary>
public sealed class SearchEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/search",
                async (string? q, int? limit, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new Search(q ?? "", limit), ct)))
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Search")
            .Produces<IReadOnlyList<SearchResult>>();
    }
}
