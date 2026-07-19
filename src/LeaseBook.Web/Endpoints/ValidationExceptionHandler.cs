using FluentValidation;
using LeaseBook.Web.Observability;
using Microsoft.AspNetCore.Diagnostics;

namespace LeaseBook.Web.Endpoints;

/// <summary>
/// Maps a <see cref="ValidationException"/> thrown by the CQRS ValidationDecorator (§C.8) to a 400
/// ProblemDetails with the <c>errors</c> dictionary. The single mapping home for dispatched messages;
/// real CQRS slices arrive in M1, but the contract is wired now.
/// </summary>
public sealed class ValidationExceptionHandler(ILogger<ValidationExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ValidationException validationException)
        {
            return false;
        }

        var errors = validationException.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

        logger.LogWarning(
            LogEvents.ValidationRejection,
            "Validation rejection on {FieldCount} field(s)",
            errors.Count);

        await ProblemResults.ValidationProblem(httpContext, errors).ExecuteAsync(httpContext);
        return true;
    }
}
