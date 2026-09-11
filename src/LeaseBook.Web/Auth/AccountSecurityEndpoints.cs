using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Endpoints;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace LeaseBook.Web.Auth;

public sealed class AccountSecurityEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");
        group.MapPost("/mfa/recovery", async (RecoveryLoginRequest request,
            SignInManager<AppUser> signIn, UserManager<AppUser> users, HttpContext http) =>
        {
            var user = await signIn.GetTwoFactorAuthenticationUserAsync();
            // Same split as POST /api/auth/mfa (#360): a null user is the partial cookie having expired,
            // which is a timed-out attempt rather than a rejected credential.
            //
            // A token mismatch and a lockout answer `invalid_credentials`; a wrong recovery code
            // answers `invalid_recovery_code` and calls AccessFailedAsync, so the code a caller sees
            // flips once the lockout trips. Unlike the password step, this endpoint does NOT collapse
            // its rejections into one answer — do not read it as doing so. It leaks nothing today
            // because reaching it at all requires the correct password, and the sign-in page renders
            // one generic literal for both, but the collapsing happens on the client, not here.
            if (user is null)
            {
                return ProblemResults.Problem(
                    http,
                    "mfa_session_expired",
                    "Your sign-in attempt timed out. Start again from the sign-in page.",
                    401);
            }
            if (!string.Equals(request.MfaToken, user.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                || await users.IsLockedOutAsync(user))
            {
                return ProblemResults.Problem(http, "invalid_credentials", "Invalid credentials.", 401);
            }
            var result = await signIn.TwoFactorRecoveryCodeSignInAsync(request.Code.Trim());
            if (!result.Succeeded)
            {
                AccountSecurityAudit.RequireSuccess(await users.AccessFailedAsync(user));
                return ProblemResults.Problem(http, "invalid_recovery_code", "Invalid recovery code.", 401);
            }
            return Results.Ok(new LoginResponse(LoginStatus.Ok, null));
        })
        .AllowAnonymous().RequireRateLimiting("auth")
        .AddEndpointFilter<ValidationEndpointFilter<RecoveryLoginRequest>>()
        .Produces<LoginResponse>();

        group.MapPost("/change-password", async (ChangePasswordRequest request,
            UserManager<AppUser> users, SignInManager<AppUser> signIn, AccountSecurityAudit audit, HttpContext http) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is null)
            {
                return ProblemResults.Problem(http, "not_authenticated", "Not authenticated.", 401);
            }
            if (!(await signIn.CheckPasswordSignInAsync(user, request.CurrentPassword, lockoutOnFailure: true)).Succeeded)
            {
                return ProblemResults.Problem(http, "invalid_credentials", "Invalid current password.", 400);
            }
            var changed = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            if (!changed.Succeeded)
            {
                return ProblemResults.Problem(http, "password_rejected",
                    "Password must contain upper and lower case letters, a number and a symbol, and be at least 12 characters.", 400);
            }
            await audit.RecordAsync(user, "password-changed", http.RequestAborted);
            await signIn.RefreshSignInAsync(user);
            return TypedResults.NoContent();
        })
        .RequireAuthorization(AuthPolicies.AuthenticatedMfaExempt).RequireRateLimiting("auth")
        .AddEndpointFilter<ValidationEndpointFilter<ChangePasswordRequest>>();
    }
}
