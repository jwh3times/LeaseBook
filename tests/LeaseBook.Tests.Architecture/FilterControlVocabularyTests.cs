using System.Text.RegularExpressions;
using LeaseBook.Modules.Reporting.Catalog;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// <c>ReportDescriptor.FilterControls</c> names the filter controls the report builder offers, keyed
/// by the literal query parameter each one binds. Nothing server-side reads the list, so a key that
/// does not match a bound parameter name fails silently — the SPA's membership test just goes false
/// and the chip never renders.
/// <para>
/// That is not hypothetical. The catalog shipped <c>"owner"</c>, <c>"property"</c> and <c>"bank"</c>
/// while the endpoint bound <c>ownerId</c>, <c>propertyId</c> and <c>bankAccountId</c>, so no report
/// could be filtered by owner, property or bank in the builder at all (#229). Every frontend test
/// stayed green because they hand-write descriptor mocks in the SPA's own vocabulary, which proves
/// only that the SPA agrees with itself. The disagreement is cross-language, so the guard has to be
/// too — the same reason <see cref="CapabilitiesJobTemplateTests"/> reads YAML from a C# test.
/// </para>
/// <para>
/// The catalog side is read from the compiled type, not parsed. Only the endpoint side is source-scanned,
/// because minimal-API query parameters exist solely as lambda parameters — there is no metadata to
/// reflect over without building a host. <see cref="Every_route_scan_finds_its_known_parameters"/>
/// fails loudly if that scan ever stops matching, so a refactor cannot quietly turn this guard green.
/// </para>
/// </summary>
public sealed class FilterControlVocabularyTests
{
    private const string EndpointsPath = "src/LeaseBook.Web/Reporting/ReportingEndpoints.cs";

    private const string PreviewRoute = "/reports/{id}/preview";
    private const string CsvRoute = "/reports/{id}/csv";
    private const string CompliancePackRoute = "/reports/compliance-pack";

    /// <summary>The catalog id served by <see cref="CompliancePackRoute"/>; every other id is a preview.</summary>
    private const string CompliancePackId = "compliance-pack";

    /// <summary>
    /// Types ASP.NET Core binds from the query string. Anything else in a lambda's parameter list is
    /// an injected service, so restricting to these keeps <c>previewService</c> and <c>httpContext</c>
    /// out of the bound set — a permissive scan would let a catalog key pass by colliding with a
    /// service parameter name.
    /// </summary>
    private static readonly HashSet<string> ScalarTypes = new(StringComparer.Ordinal)
    {
        "int", "long", "decimal", "bool", "string", "Guid", "DateOnly", "DateTime",
    };

    private static readonly Regex RouteTemplateParameter = new(@"\{(?<name>[^:}]+)", RegexOptions.Compiled);

    [Fact]
    public void Every_filter_control_key_is_a_query_parameter_its_endpoint_binds()
    {
        var preview = BoundQueryParameters(PreviewRoute);
        var compliance = BoundQueryParameters(CompliancePackRoute);

        var unbound = ReportCatalog.All
            .SelectMany(descriptor =>
            {
                var bound = descriptor.Id == CompliancePackId ? compliance : preview;
                return descriptor.FilterControls
                    .Where(key => !bound.Contains(key))
                    .Select(key => $"{descriptor.Id}: '{key}'");
            })
            .ToList();

        unbound.ShouldBeEmpty(
            $"""
             Every ReportDescriptor.FilterControls key must be a query parameter its endpoint binds,
             because the SPA tests membership against these exact strings to decide which filter chips
             to render. A key that matches nothing hides its control with no error anywhere.

             Unbound keys:
               {string.Join("\n  ", unbound)}

             Bound by {PreviewRoute}: {string.Join(", ", preview.Order(StringComparer.Ordinal))}
             Bound by {CompliancePackRoute}: {string.Join(", ", compliance.Order(StringComparer.Ordinal))}
             """);
    }

    [Fact]
    public void The_preview_and_csv_routes_bind_the_same_query_parameters()
    {
        // The builder sends one filter set to both routes — it offers a single row of chips and then
        // either previews or exports. If the two signatures drift, a filter that shapes the preview
        // would be dropped from the CSV of the same report, which reads as an export bug rather than
        // a binding one.
        BoundQueryParameters(CsvRoute)
            .ShouldBe(BoundQueryParameters(PreviewRoute), ignoreOrder: true);
    }

    [Fact]
    public void Every_route_scan_finds_its_known_parameters()
    {
        // A source scan that stops matching goes green, not red. These anchors are the parameters
        // whose absence means the parse broke rather than the endpoint changed.
        BoundQueryParameters(PreviewRoute)
            .ShouldBe(["year", "month", "ownerId", "propertyId", "bankAccountId", "asOf"], ignoreOrder: true);
        BoundQueryParameters(CompliancePackRoute)
            .ShouldBe(["bankAccountId", "from", "to"], ignoreOrder: true);
    }

    /// <summary>
    /// The query-bindable parameter names of the lambda mapped to <paramref name="route"/>: scalar
    /// parameters minus the route template's own placeholders.
    /// </summary>
    private static IReadOnlyCollection<string> BoundQueryParameters(string route)
    {
        var source = RepositorySource.Current.File(EndpointsPath).Text;
        var routeLiteral = $"\"{route}\"";
        var routeAt = source.IndexOf(routeLiteral, StringComparison.Ordinal);
        routeAt.ShouldBeGreaterThanOrEqualTo(0, $"{EndpointsPath} no longer maps {route}.");

        var parameterList = LambdaParameterList(source, routeAt + routeLiteral.Length, route);
        var routeParameters = RouteTemplateParameter.Matches(route)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. parameterList
                .Split(',')
                .Select(parameter => parameter.Split((char[])['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                .Where(tokens => tokens.Length == 2 && ScalarTypes.Contains(tokens[0].TrimEnd('?')))
                .Select(tokens => tokens[1])
                .Where(name => !routeParameters.Contains(name)),
        ];
    }

    /// <summary>
    /// The text between the parentheses of the handler lambda that follows <paramref name="from"/>,
    /// found by balancing parentheses so a nested call in a default value cannot end it early.
    /// </summary>
    private static string LambdaParameterList(string source, int from, string route)
    {
        var open = source.IndexOf('(', from);
        open.ShouldBeGreaterThanOrEqualTo(0, $"No handler lambda follows the {route} mapping.");

        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            depth += source[index] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[(open + 1)..index];
            }
        }

        throw new InvalidOperationException($"Unbalanced parentheses in the {route} handler lambda.");
    }
}
