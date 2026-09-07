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
            if (user is null || !string.Equals(request.MfaToken, user.Id.ToString(), StringComparison.OrdinalIgnoreCase)
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
