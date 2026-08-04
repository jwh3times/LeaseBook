using LeaseBook.Modules.Operations.Runs;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Observability;
using Microsoft.AspNetCore.Diagnostics;

namespace LeaseBook.Web.Endpoints;

/// <summary>
/// Maps a typed <see cref="OperationsDomainException"/> from the run pipeline to an RFC 7807
/// ProblemDetails on ADR-025's contract, via <see cref="ProblemResults"/>. One handler for the
/// module, switching on <see cref="OperationsDomainException.Code"/> — the shape
/// <see cref="AccountingExceptionHandler"/> has had since M1.
/// <para>
/// <b>A handler rather than a try/catch in the endpoint.</b> Catching at the endpoint would let the
/// request's ambient transaction COMMIT, since no exception would reach <c>OrgContextMiddleware</c>.
/// Nothing has been written at the point of either throw today, so that would be harmless now and
/// quietly wrong the moment anything in a confirm writes before the guards. Letting it propagate
/// makes rollback the mechanism instead of an argument about ordering, and it keeps the error
/// contract in the one place ADR-025 put it.
/// </para>
/// <para>
/// <b>Warning, not Error.</b> These are expected, typed rejections the domain deliberately raised —
/// the operator re-previews, or makes a decision — so they follow the same level rule as
/// <see cref="AccountingExceptionHandler"/> rather than paging anyone.
/// </para>
/// </summary>
public sealed class OperationsExceptionHandler(
    ILogger<OperationsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not OperationsDomainException domain)
        {
            return false;
        }

        // Both capability conflicts are 409s, and the default arm keeps a future Operations error
        // from falling through to the terminal handler's uncoded 500 while someone forgets an arm.
        // The event id is what separates them for alerting: 1300 is worth acting on only as a rate,
        // 1301 one at a time.
        var (status, eventId) = domain.Code switch
        {
            CapabilitiesChangedException.ErrorCode =>
                (StatusCodes.Status409Conflict, LogEvents.CapabilityVersionConflict),
            CapabilitiesChangedSincePriorRunException.ErrorCode =>
                (StatusCodes.Status409Conflict, LogEvents.CapabilityCrossRunConflict),
            _ => (StatusCodes.Status409Conflict, LogEvents.DomainRejection),
        };

        // A CONSTANT template with the varying parts as parameters, matching
        // AccountingExceptionHandler: every log aggregation in this repo groups on the template, and
        // interpolating the description into a placeholder would give 1300 and 1301 no stable
        // template to group by at all.
        //
        // No version token or money-path state in it either. Both are internal identifiers, and the
        // run's own telemetry span already carries the resolved version for the engineer side; this
        // log correlates to it through the correlation id ProblemResults attaches.
        logger.LogWarning(
            eventId,
            "Operations rejection {Code} mapped to {Status} for {ExceptionType}",
            domain.Code, status, domain.GetType().Name);

        await ProblemResults.Problem(
                httpContext,
                code: domain.Code,
                detail: domain.Message,
                status: status)
            .ExecuteAsync(httpContext);

        return true;
    }
}
