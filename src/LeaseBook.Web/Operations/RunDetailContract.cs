using System.Text.Json.Nodes;
using LeaseBook.Modules.Operations.Domain;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LeaseBook.Web.Operations;

/// <summary>
/// Publishes the run outcome and the run reason codes as enums in the OpenAPI document (#573), the way
/// <c>BudgetTasks</c> publishes the telemetry task list: <c>[AllowedValues]</c> is not emitted, so a
/// schema transformer is what carries the list to the generated client. The SPA's wording map is
/// typed as a complete record over the generated reason type, so a code added here without wording
/// fails the SPA typecheck instead of reaching a screen as raw text.
/// </summary>
public static class RunDetailContract
{
    /// <summary>The run outcomes, as <c>RunItemStatus</c> names them on the wire.</summary>
    public static IReadOnlyList<string> Outcomes { get; } = [.. Enum.GetNames<RunItemStatus>()];

    public static Task PublishEnums(
        OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(BulkRunItemSpa))
        {
            Publish(schema, "outcome", Outcomes, nullable: false);
            Publish(schema, "reason", RunReasons.AllCodes, nullable: true);
        }
        else if (type == typeof(PreviewRowSpa))
        {
            Publish(schema, "excludedReason", RunReasons.AllCodes, nullable: true);
        }

        return Task.CompletedTask;
    }

    private static void Publish(OpenApiSchema schema, string property, IReadOnlyList<string> values, bool nullable)
    {
        if (schema.Properties?.TryGetValue(property, out var found) != true || found is not OpenApiSchema target)
        {
            throw new InvalidOperationException(
                $"The OpenAPI schema has no '{property}' property to publish as an enum.");
        }

        List<JsonNode?> members = [.. values.Select(value => (JsonNode?)JsonValue.Create(value))];
        if (nullable)
        {
            members.Add(null);
        }

        target.Enum = members!;
    }
}
