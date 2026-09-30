using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Observability;
using Microsoft.AspNetCore.Diagnostics;

namespace LeaseBook.Web.Endpoints;

/// <summary>
/// Maps a request the endpoint binder could not read — malformed JSON, a wrong-typed value, a missing
/// required parameter, or an unknown member on a DTO that disallows them (a stale client still sending
/// the pre-#468 <c>memo</c>) — to a contract-shaped 400 (ADR-025).
/// <para>
/// This only works because <c>Program</c> turns <c>RouteHandlerOptions.ThrowOnBadRequest</c> on in every
/// environment. Left at the framework default, it is on only in Development, and everywhere else the
/// binder writes a bare, bodyless 400 itself. That would give the operator no <c>code</c> and no
/// reference to quote, and nothing would reach this handler to fix it.
/// <c>MiddlewareErrorContractTests</c> drives this path with that Development default removed.
/// </para>
/// <para>
/// Registered after the typed handlers, so a route with its own mapping keeps it
/// (<c>PaymentExceptionHandler</c> answers <c>invalid_payment_request</c>), and before the terminal
/// handler, which would otherwise turn a client error into a 500.
/// </para>
/// </summary>
public sealed class BadRequestExceptionHandler(ILogger<BadRequestExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException { StatusCode: StatusCodes.Status400BadRequest })
        {
            return false;
        }

        // The exception message names the parameter type and the JSON path. That is useful in the log
        // and is exactly what ADR-025 keeps out of the response detail.
        logger.LogWarning(LogEvents.ValidationRejection, exception, "Unreadable request rejected");

        await ProblemResults.Problem(
            httpContext,
            code: "invalid_request",
            detail: "The request could not be read. A field may be missing, misspelled, or have a value of the wrong kind.",
            status: StatusCodes.Status400BadRequest).ExecuteAsync(httpContext);
        return true;
    }
}
