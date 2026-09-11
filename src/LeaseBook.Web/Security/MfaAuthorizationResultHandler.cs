using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace LeaseBook.Web.Security;

/// <summary>
/// Turns an MFA-enrollment authorization failure into a 403 problem-details that tells the caller
/// what to do about it. All other results defer to the default handler — in particular a *role*
/// denial stays a bare 403, and `AuthorizationMatrixTests` pins that, because the problem body
/// written here is what makes a problem body on a 403 mean MFA enforcement specifically.
///
/// Built through <see cref="ProblemResults"/> like every other error response. It used to be
/// hand-rolled with <c>WriteAsJsonAsync</c>, which shipped a body carrying neither <c>code</c> nor
/// <c>correlationId</c> for as long as the feature existed — invisible to <c>ErrorContractTests</c>,
/// which reads IL for direct factory calls and so cannot see a response that never makes one
/// (#361, ADR-025). `MiddlewareErrorContractTests` now checks the emitted responses instead.
/// </summary>
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
            // The `urn:leasebook:error:*` type is gone with the hand-rolled body: nothing read it, and
            // `code` is the machine contract everywhere else — two spellings of one identifier is the
            // drift ADR-025's single factory exists to prevent. The detail names no route, because the
            // SPA renders it verbatim and `RouteGuard` already sends an unenrolled admin to the page.
            await ProblemResults
                .TypedProblem(
                    context,
                    code: "mfa_enrollment_required",
                    detail: "Two-factor authentication must be set up on your account before you can continue.",
                    status: StatusCodes.Status403Forbidden)
                .ExecuteAsync(context);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
