using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LeaseBook.SharedKernel.Endpoints;

/// <summary>
/// The single problem-details factory (ADR-025). Stamps the machine-readable <c>code</c> and the
/// <c>correlationId</c> on every error response so the operator has a reference to quote and an
/// engineer has a key to search App Insights on. Never call <see cref="Results.Problem"/>,
/// <see cref="TypedResults.Problem"/> or <see cref="Results.ValidationProblem"/> directly —
/// <c>ErrorContractTests</c> fails the build if you do.
/// </summary>
public static class ProblemResults
{
    /// <summary>
    /// The W3C trace id — the same value App Insights indexes as <c>operation_Id</c>, which is why
    /// this is the id we surface rather than a freshly minted GUID.
    /// </summary>
    public static string CorrelationId(HttpContext httpContext) =>
        Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

    /// <summary>For typed-union delegates (<c>Results&lt;Ok&lt;T&gt;, …, ProblemHttpResult&gt;</c>).</summary>
    public static ProblemHttpResult TypedProblem(
        HttpContext httpContext,
        string code,
        string detail,
        int status,
        IDictionary<string, object?>? extensions = null)
    {
        var merged = extensions is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(extensions);

        merged["code"] = code;
        merged["correlationId"] = CorrelationId(httpContext);

        return TypedResults.Problem(detail: detail, statusCode: status, title: code, extensions: merged);
    }

    public static IResult Problem(
        HttpContext httpContext,
        string code,
        string detail,
        int status,
        IDictionary<string, object?>? extensions = null)
        => TypedProblem(httpContext, code, detail, status, extensions);

    /// <summary>
    /// The validation shape with the contract stamped on. Two live emitters share this:
    /// ValidationExceptionHandler (CQRS slices) and ValidationEndpointFilter (auth DTOs).
    /// </summary>
    public static IResult ValidationProblem(HttpContext httpContext, IDictionary<string, string[]> errors)
        => Results.ValidationProblem(
            errors,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "validation_failed",
                ["correlationId"] = CorrelationId(httpContext),
            });
}
