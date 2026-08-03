using System.Reflection;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Web.Endpoints;

/// <summary>Health and meta endpoints (§C.7). Anonymous — used by container probes and CI.</summary>
public sealed class MetaEndpoints : IEndpointModule
{
    /// <summary>
    /// Readiness. Distinct from <c>/api/health</c>, which is liveness: the process is up. This one
    /// answers "may this replica take traffic", and it is 503 until the capability seam has been
    /// proven reachable (<see cref="CapabilityReadinessCheck"/>).
    /// </summary>
    public const string ReadinessPath = "/api/health/ready";

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () => TypedResults.Ok(new HealthResponse("ok", Version)))
            .AllowAnonymous()
            .WithTags("Meta")
            .Produces<HealthResponse>();

        // Filtered to the `ready` tag so a later liveness- or diagnostic-tagged check cannot start
        // pulling replicas out of rotation by being registered. Excluded from the OpenAPI document
        // because the generated SPA client has no use for a probe endpoint, and adding a route to the
        // document would move the schema-drift gate for no consumer.
        app.MapHealthChecks(
                ReadinessPath,
                new HealthCheckOptions { Predicate = check => check.Tags.Contains(CapabilityReadinessCheck.ReadyTag) })
            .AllowAnonymous()
            .WithTags("Meta")
            .ExcludeFromDescription();
    }

    private static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
}

public sealed record HealthResponse(string Status, string Version);
