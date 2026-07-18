using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LeaseBook.Web.Security;

/// <summary>Turns an MFA-enrollment authorization failure into a 403 problem-details that points the
/// caller at the enrollment endpoint. All other results defer to the default handler.</summary>
public sealed class MfaAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        var failedMfa = authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<MfaEnrolledRequirement>().Any() == true;

        if (failedMfa)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Multi-factor authentication required.",
                Detail = "Enroll an authenticator app via POST /api/auth/mfa/enroll before continuing.",
                Type = "urn:leasebook:error:mfa-enrollment-required",
            }, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
