using System.Reflection;
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
        app.MapGet("/api/health", () => TypedResults.Ok(new HealthResponse("ok", Version)))
            .AllowAnonymous()
            .WithTags("Meta")
            .Produces<HealthResponse>();

        // Filtered to the `ready` tag so a later liveness- or diagnostic-tagged check cannot start
        // pulling replicas out of rotation by being registered. Excluded from the OpenAPI document
        // because the generated SPA client has no use for a probe endpoint, and adding a route to the
        // document would move the schema-drift gate for no consumer.
        //
        // The response writer names each check and its status. There are now two independent reasons
        // to be 503 — an unreachable capability seam and unseeded roles — and the framework default
        // writes only the aggregate word "Unhealthy", which would tell an operator staring at a
        // rolling deploy nothing about which one to chase. Probes read the status code and ignore the
        // body, so this costs them nothing.
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

    /// <summary>
    /// Plain text, one line per check, deliberately not JSON: this is read by a human during an
    /// incident and by nothing else. It is excluded from the OpenAPI document, so no generated client
    /// depends on its shape.
    /// </summary>
    private static Task WriteReadinessAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "text/plain";

        var lines = report.Entries
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}: {entry.Value.Status} — {entry.Value.Description}");

        return context.Response.WriteAsync(
            string.Join(Environment.NewLine, [$"status: {report.Status}", .. lines]));
    }

    private static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
}

public sealed record HealthResponse(string Status, string Version);
