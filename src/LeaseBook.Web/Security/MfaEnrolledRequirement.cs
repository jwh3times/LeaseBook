using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace LeaseBook.Web.Security;

/// <summary>Requires a PMAdmin principal to have completed TOTP enrollment. Non-admins and, when the
/// gate is off, everyone, pass trivially. Fails closed (no <c>Succeed</c>) → 403.</summary>
public sealed class MfaEnrolledRequirement : IAuthorizationRequirement;

public sealed class MfaEnrolledAuthorizationHandler(IOptions<AuthOptions> options)
    : AuthorizationHandler<MfaEnrolledRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, MfaEnrolledRequirement requirement)
    {
        if (!options.Value.EnforceAdminMfa
            || !context.User.IsInRole(Roles.PMAdmin)
            || context.User.HasClaim(AuthClaims.MfaEnrolled, "true"))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
