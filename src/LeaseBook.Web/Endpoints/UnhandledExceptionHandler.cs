using LeaseBook.Web.Observability;
using Microsoft.AspNetCore.Diagnostics;

namespace LeaseBook.Web.Endpoints;

/// <summary>
/// The terminal handler (ADR-025), registered last: everything the typed handlers decline lands
/// here. Logs the full exception at Error with the correlation id, and returns a generic 500 that
/// carries nothing but that reference. The exception message, type, and stack trace never cross
/// the wire.
/// </summary>
public sealed class UnhandledExceptionHandler(ILogger<UnhandledExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var correlationId = ProblemResults.CorrelationId(httpContext);

        logger.LogError(
            LogEvents.UnhandledException,
            exception,
            "Unhandled exception. CorrelationId={CorrelationId} Method={Method} Path={Path}",
            correlationId, httpContext.Request.Method, httpContext.Request.Path.Value);

        await ProblemResults.Problem(
                httpContext,
                code: "internal_error",
                detail: $"An unexpected error occurred. Quote reference {correlationId} if you contact support.",
                status: StatusCodes.Status500InternalServerError)
            .ExecuteAsync(httpContext);

        return true;
    }
}
