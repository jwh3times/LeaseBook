using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LeaseBook.Web.Endpoints;

/// <summary>Health and meta endpoints (§C.7). Anonymous — used by container probes and CI.</summary>
public sealed class MetaEndpoints : IEndpointModule
{
    /// <summary>
    /// Readiness. Distinct from <c>/api/health</c>, which is liveness: the process is up. This one
    /// answers "may this replica take traffic", and it is 503 until <b>both</b> preconditions hold:
    /// the capability seam has been proven reachable (<see cref="CapabilityReadinessCheck"/>) and the
    /// fixed roles have been seeded (<see cref="RoleSeedingReadinessCheck"/>).
    /// </summary>
    public const string ReadinessPath = "/api/health/ready";

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () => TypedResults.Ok(new HealthResponse("ok")))
            .AllowAnonymous()
            .WithTags("Meta")
            .Produces<HealthResponse>();

        // Filtered to the `ready` tag so a later liveness- or diagnostic-tagged check cannot start
        // pulling replicas out of rotation by being registered. Excluded from the OpenAPI document
        // because the generated SPA client has no use for a probe endpoint, and adding a route to the
        // document would move the schema-drift gate for no consumer.
        //
        // The body is the aggregate status only: the endpoint is anonymous on the public ingress, and
        // probes read the status code. Which check failed — the thing an operator needs during a
        // rolling deploy, with two independent reasons to be 503 — goes to the log instead.
        app.MapHealthChecks(
                ReadinessPath,
                new HealthCheckOptions
                {
                    Predicate = check => check.Tags.Contains(CapabilityReadinessCheck.ReadyTag),
                    ResponseWriter = WriteReadinessAsync,
                })
            .AllowAnonymous()
            .WithTags("Meta")
            .ExcludeFromDescription();
    }

    /// <summary>Plain text, deliberately not JSON, and deliberately just the aggregate status.</summary>
    private static Task WriteReadinessAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync($"status: {report.Status}");
    }
}

public sealed record HealthResponse(string Status);
