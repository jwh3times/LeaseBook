using LeaseBook.Modules.Operations.Runs;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Observability;
using Microsoft.AspNetCore.Diagnostics;

namespace LeaseBook.Web.Endpoints;

/// <summary>
/// Maps <see cref="CapabilitiesChangedException"/> — a run confirmed against a capability set that
/// moved after its preview — to a 409 with the <c>capabilities_changed</c> code (ADR-028), on
/// ADR-025's contract via <see cref="ProblemResults"/>.
/// <para>
/// <b>A handler rather than a try/catch in the endpoint.</b> Catching it at the endpoint would let
/// the request's ambient transaction COMMIT, since no exception would reach
/// <c>OrgContextMiddleware</c>. Nothing has been written at the point of the throw, so that would be
/// harmless today and quietly wrong the moment anything in a confirm writes before the guard. Letting
/// it propagate makes rollback the mechanism instead of an argument about ordering, and it keeps the
/// error contract in the one place ADR-025 put it.
/// </para>
/// <para>
/// <b>Warning, not Error.</b> This is an expected, typed rejection the domain deliberately raised —
/// the operator re-previews and continues — so it follows the same level rule as
/// <see cref="AccountingExceptionHandler"/> rather than paging anyone.
/// </para>
/// </summary>
public sealed class CapabilitiesChangedExceptionHandler(
    ILogger<CapabilitiesChangedExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not CapabilitiesChangedException changed)
        {
            return false;
        }

        // No version token in the log line either. Both sides are opaque digests that identify a
        // SET, not a request; the trace this log correlates to already carries the resolved version
        // as a span tag (RunEngine.ConfirmAsync), which is the searchable half.
        logger.LogWarning(
            LogEvents.CapabilityVersionConflict,
            "Run confirm rejected: the capability set changed between preview and confirm.");

        await ProblemResults.Problem(
                httpContext,
                code: "capabilities_changed",
                detail: changed.Message,
                status: StatusCodes.Status409Conflict)
            .ExecuteAsync(httpContext);

        return true;
    }
}
