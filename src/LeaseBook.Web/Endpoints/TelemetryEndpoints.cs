using System.Text.Json.Nodes;
using FluentValidation;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.SharedKernel.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi;

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
            .AddEndpointFilter<ValidationEndpointFilter<BudgetTelemetryRequest>>()
            .WithTags("Telemetry");
    }
}

/// <summary>
/// The budgeted tasks the SPA reports — the UX contract's instrumented flows. The sink accepts only
/// these, and the OpenAPI document publishes them as the <c>task</c> enum, so the generated client
/// types <c>trackInteraction</c> against this list: a task added on one side only fails the SPA
/// typecheck instead of being rejected silently by a fire-and-forget call.
/// </summary>
public static class BudgetTasks
{
    public static readonly IReadOnlyList<string> All =
    [
        "record-payment",
        "add-charge",
        "owner-balances-visible",
        "start-reconcile",
        "entity-jump",
        "rent-run-confirm",
        "latefee-run-confirm",
        "disbursement-run-confirm",
    ];

    /// <summary>
    /// Publishes <see cref="All"/> as the <c>task</c> enum. <c>[AllowedValues]</c> would say the same
    /// thing but is not emitted into the document, so this transformer is what carries the list to
    /// the generated client.
    /// </summary>
    public static Task PublishAsTaskEnum(
        OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(BudgetTelemetryRequest)
            && schema.Properties?.TryGetValue("task", out var task) == true
            && task is OpenApiSchema taskSchema)
        {
            taskSchema.Enum = [.. All.Select(name => (JsonNode)JsonValue.Create(name))];
        }

        return Task.CompletedTask;
    }
}

/// <summary>A budgeted-interaction sample (no amounts/PII — tags only).</summary>
public sealed record BudgetTelemetryRequest(string Task, int Interactions, bool? Met);

public sealed class BudgetTelemetryRequestValidator : AbstractValidator<BudgetTelemetryRequest>
{
    public BudgetTelemetryRequestValidator()
    {
        // Ordinal and exact: the allowlist is the whole bound, including length, so nothing else
        // about the string needs checking before it becomes a span tag.
        RuleFor(x => x.Task).Must(task => BudgetTasks.All.Contains(task, StringComparer.Ordinal))
            .WithMessage("task must be a budgeted task.");
        RuleFor(x => x.Interactions).GreaterThanOrEqualTo(0);
    }
}
