using LeaseBook.Modules.Payments.Features.RefundChecks;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Diagnostics;

namespace LeaseBook.Web.Payments;

public sealed class PaymentExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is BadHttpRequestException { StatusCode: 400 }
            && context.Request.Path.StartsWithSegments("/api/portal/tenant/payments"))
        {
            await ProblemResults.Problem(context, "invalid_payment_request", "The payment request is invalid.", 400).ExecuteAsync(context);
            return true;
        }
        if (exception is RefundCheckConflictException refund)
        {
            await ProblemResults.Problem(context, refund.Code, refund.Message, 409).ExecuteAsync(context);
            return true;
        }
        if (exception is not (PaymentConflictException or PaymentUnavailableException)) { return false; }
        var conflict = exception is PaymentConflictException;
        await ProblemResults.Problem(context, conflict ? "payment_conflict" : "payment_unavailable",
            conflict ? "This payment request conflicts with an existing operation." : "Simulated payments are unavailable for this account.",
            conflict ? 409 : 422).ExecuteAsync(context);
        return true;
    }
}
