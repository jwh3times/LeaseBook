using System.Reflection;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Web.Endpoints;

/// <summary>Health and meta endpoints (§C.7). Anonymous — used by container probes and CI.</summary>
public sealed class MetaEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () => TypedResults.Ok(new HealthResponse("ok", Version)))
            .AllowAnonymous()
            .WithTags("Meta")
            .Produces<HealthResponse>();
    }

    private static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
}

public sealed record HealthResponse(string Status, string Version);
