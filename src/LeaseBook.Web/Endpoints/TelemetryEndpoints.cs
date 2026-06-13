using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.SharedKernel.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Web.Endpoints;

/// <summary>
/// Click-budget telemetry sink (§C.8 / P47). The SPA posts how many interactions a budgeted task took;
/// this emits a tags-only OTel <c>ux.budget</c> span on the <c>LeaseBook</c> ActivitySource — <b>no
/// amounts, no PII</b> (just <c>task</c>, <c>interactions</c>, <c>met</c>). Staff-level; fire-and-forget
/// from the client, so it returns 204 and never blocks the UI.
/// </summary>
public sealed class TelemetryEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/telemetry/budget",
                (BudgetTelemetryRequest body) =>
                {
                    using var activity = LeaseBookTelemetry.Source.StartActivity("ux.budget");
                    activity?.SetTag("task", body.Task);
                    activity?.SetTag("interactions", body.Interactions);
                    activity?.SetTag("met", body.Met);
                    return TypedResults.NoContent();
                })
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Telemetry");
    }
}

/// <summary>A budgeted-interaction sample (no amounts/PII — tags only).</summary>
public sealed record BudgetTelemetryRequest(string Task, int Interactions, bool? Met);
