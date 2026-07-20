using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Observability;
using Microsoft.AspNetCore.Diagnostics;

namespace LeaseBook.Web.Endpoints;

/// <summary>
/// The terminal handler (ADR-025), registered last: everything the typed handlers decline lands
/// here. Logs the full exception at Error with the correlation id, and returns a generic 500 that
/// carries nothing but that reference. The exception message, type, and stack trace never cross
/// the wire. Request method and path are deliberately absent from the log template — they are
/// caller-supplied (CWE-117 log forging) and the trace-correlated request span already carries
/// them.
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
            "Unhandled exception. CorrelationId={CorrelationId}",
            correlationId);

        await ProblemResults.Problem(
                httpContext,
                code: "internal_error",
                detail: $"An unexpected error occurred. Quote reference {correlationId} if you contact support.",
                status: StatusCodes.Status500InternalServerError)
            .ExecuteAsync(httpContext);

        return true;
    }
}
