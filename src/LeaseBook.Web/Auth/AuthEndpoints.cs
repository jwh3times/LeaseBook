using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Endpoints;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Auth;

/// <summary>
/// Auth API (§C.6) as a minimal-API endpoint module (P22). Thin handlers calling Identity managers
/// directly; request DTOs validated by <see cref="ValidationEndpointFilter{T}"/>; errors as
/// ProblemDetails. Login never reveals whether an email exists.
/// </summary>
public sealed class AuthEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            http.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
            {
                HttpOnly = false, // the SPA must read it to echo as the X-XSRF-TOKEN header
                SameSite = SameSiteMode.Lax,
                Secure = http.Request.IsHttps,
            });
            return TypedResults.NoContent();
        }).AllowAnonymous();

        group.MapPost("/login", async (
            LoginRequest request, SignInManager<AppUser> signInManager, UserManager<AppUser> userManager,
            PasswordTimingEqualizer timing, HttpContext httpContext) =>
        {
            // Every arm below that reaches the generic 401 must cost the same. Identity verifies a
            // password hash only after it has found a user and cleared the pre-sign-in checks, so an
            // unknown email, a locked-out account and a not-allowed account each skipped the most
            // expensive step and answered far faster than a wrong password did. The uniform message
            // made the three indistinguishable in the body; the clock told them apart anyway, which
            // is an account-existence oracle by another route. `timing.SpendVerification` pays the
            // missing cost on exactly those arms.
            var user = await userManager.FindByEmailAsync(request.Email);
            if (user is null)
            {
                timing.SpendVerification(request.Password);
            }
            else
            {
                // Predicted BEFORE the call, because the result cannot be read backwards for this.
                // `LockedOut` is returned from two places: from the pre-sign-in check, ahead of the
                // hasher, and again *after* the hasher when this very attempt trips the lockout
                // counter. Treating every `LockedOut` as unpaid charges that second case twice, which
                // made the lock-tripping attempt cost about double and marked the exact moment an
                // account locks — a sharper signal than the one being removed. A missing stored hash
                // also returns `Failed` without the hasher running, so it is predicted here too
                // rather than inferred from the enum.
                var hasherWillRun = user.PasswordHash is not null
                    && await signInManager.CanSignInAsync(user)
                    && !await userManager.IsLockedOutAsync(user);

                var result = await signInManager.PasswordSignInAsync(
                    user, request.Password, isPersistent: false, lockoutOnFailure: true);
                if (result.Succeeded)
                {
                    return Results.Ok(new LoginResponse(LoginStatus.Ok, null));
                }

                if (result.RequiresTwoFactor)
                {
                    return Results.Ok(new LoginResponse(LoginStatus.MfaRequired, user.Id.ToString()));
                }

                if (!hasherWillRun)
                {
                    timing.SpendVerification(request.Password);
                }
            }

            // Generic message for bad credentials, lockout, and unknown email alike.
            return ProblemResults.Problem(
                httpContext,
                code: "invalid_credentials",
                detail: "Invalid credentials.",
                status: StatusCodes.Status401Unauthorized);
        })
        .AddEndpointFilter<ValidationEndpointFilter<LoginRequest>>()
        .Produces<LoginResponse>()
        .AllowAnonymous()
        .RequireRateLimiting("auth");

        group.MapPost("/mfa", async (
            MfaRequest request, SignInManager<AppUser> signInManager, HttpContext httpContext) =>
        {
            var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
            // A null user means the partial cookie from the password step is gone — the sign-in attempt
            // timed out. That is not a rejected credential, and answering `invalid_credentials` told
            // someone who simply took too long that their password was wrong (#360). It gets its own
            // code because the correct next action differs: start again, not retype the code. There is
            // nothing to enumerate here — no credential was judged — so this detail is safe to render.
            if (user is null)
            {
                return ProblemResults.Problem(
                    httpContext,
                    code: "mfa_session_expired",
                    detail: "Your sign-in attempt timed out. Start again from the sign-in page.",
                    status: StatusCodes.Status401Unauthorized);
            }

            // A token that does not match the partial session is a client sending the wrong thing, not
            // a timeout. It stays generic, with the password step's copy.
            if (!string.Equals(request.MfaToken, user.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return ProblemResults.Problem(
                    httpContext,
                    code: "invalid_credentials",
                    detail: "Invalid credentials.",
                    status: StatusCodes.Status401Unauthorized);
            }

            var result = await signInManager.TwoFactorAuthenticatorSignInAsync(
                request.Code, isPersistent: false, rememberClient: false);
            return result.Succeeded
                ? Results.Ok(new LoginResponse(LoginStatus.Ok, null))
                : ProblemResults.Problem(
                    httpContext,
                    code: "invalid_mfa_code",
                    detail: "Invalid code.",
                    status: StatusCodes.Status401Unauthorized);
        })
        .AddEndpointFilter<ValidationEndpointFilter<MfaRequest>>()
        .Produces<LoginResponse>()
        .AllowAnonymous()
        .RequireRateLimiting("auth");

        group.MapPost("/logout", async (SignInManager<AppUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return TypedResults.NoContent();
        }).RequireAuthorization(AuthPolicies.AuthenticatedMfaExempt);

        group.MapGet("/me", async (HttpContext http, UserManager<AppUser> userManager, AppDbContext db, Microsoft.Extensions.Options.IOptions<AuthOptions> options) =>
        {
            var user = await userManager.GetUserAsync(http.User);
            if (user is null)
            {
                return ProblemResults.Problem(
                    http,
                    code: "not_authenticated",
                    detail: "Not authenticated.",
                    status: StatusCodes.Status401Unauthorized);
            }

            var roles = await userManager.GetRolesAsync(user);
            var orgName = await db.Orgs
                .Where(o => o.Id == user.OrgId)
                .Select(o => o.Name)
                .FirstOrDefaultAsync(http.RequestAborted);

            return Results.Ok(new MeResponse(
                user.Id, user.DisplayName, user.Email, roles.FirstOrDefault(), user.OrgId, orgName, user.TwoFactorEnabled,
                options.Value.EnforceAdminMfa && roles.Contains(Roles.PMAdmin) && !user.TwoFactorEnabled));
        })
        .Produces<MeResponse>()
        .RequireAuthorization(AuthPolicies.AuthenticatedMfaExempt);

        group.MapPost("/mfa/enroll", async (HttpContext http, UserManager<AppUser> userManager, SignInManager<AppUser> signInManager) =>
        {
            var user = await userManager.GetUserAsync(http.User);
            if (user is null)
            {
                return ProblemResults.Problem(
                    http,
                    code: "not_authenticated",
                    detail: "Not authenticated.",
                    status: StatusCodes.Status401Unauthorized);
            }

            // Enrollment is first-time setup, not a recovery or authenticator replacement path.
            // Read persisted state rather than the cookie's potentially stale mfa_enrolled claim.
            if (await userManager.GetTwoFactorEnabledAsync(user))
            {
                return ProblemResults.Problem(
                    http,
                    code: "mfa_already_enrolled",
                    detail: "Multi-factor authentication is already enrolled.",
                    status: StatusCodes.Status409Conflict);
            }

            var key = await userManager.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrEmpty(key))
            {
                AccountSecurityAudit.RequireSuccess(await userManager.ResetAuthenticatorKeyAsync(user));
                await signInManager.RefreshSignInAsync(user);
                key = await userManager.GetAuthenticatorKeyAsync(user);
            }

            http.Response.Headers.CacheControl = "no-store";
            var account = user.Email ?? user.UserName ?? user.Id.ToString();
            return Results.Ok(new EnrollResponse(BuildOtpauthUri("LeaseBook", account, key!), key!));
        })
        .Produces<EnrollResponse>()
        .RequireAuthorization(AuthPolicies.AuthenticatedMfaExempt);

        group.MapPost("/mfa/enroll/confirm", async (
            ConfirmMfaRequest request, HttpContext http, UserManager<AppUser> userManager, SignInManager<AppUser> signInManager, AccountSecurityAudit audit) =>
        {
            var user = await userManager.GetUserAsync(http.User);
            if (user is null)
            {
                return ProblemResults.Problem(
                    http,
                    code: "not_authenticated",
                    detail: "Not authenticated.",
                    status: StatusCodes.Status401Unauthorized);
            }

            if (user.TwoFactorEnabled)
            {
                return ProblemResults.Problem(http, "mfa_already_enrolled",
                    "Multi-factor authentication is already enrolled.", StatusCodes.Status409Conflict);
            }

            var valid = await userManager.VerifyTwoFactorTokenAsync(
                user, userManager.Options.Tokens.AuthenticatorTokenProvider, request.Code);
            if (!valid)
            {
                return ProblemResults.Problem(
                    http,
                    code: "invalid_mfa_code",
                    detail: "Invalid code.",
                    status: StatusCodes.Status400BadRequest);
            }

            AccountSecurityAudit.RequireSuccess(await userManager.SetTwoFactorEnabledAsync(user, true));
            var codes = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToArray()
                ?? throw new InvalidOperationException("Recovery code generation failed.");
            AccountSecurityAudit.RequireSuccess(await userManager.UpdateSecurityStampAsync(user));
            await audit.RecordAsync(user, "mfa-enrolled", http.RequestAborted);
            await signInManager.RefreshSignInAsync(user);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new RecoveryCodesResponse(codes));
        })
        .AddEndpointFilter<ValidationEndpointFilter<ConfirmMfaRequest>>()
        .Produces<RecoveryCodesResponse>()
        .RequireRateLimiting("auth")
        .RequireAuthorization(AuthPolicies.AuthenticatedMfaExempt);
    }

    private static string BuildOtpauthUri(string issuer, string account, string secret)
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        var encodedIssuer = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={secret}&issuer={encodedIssuer}&digits=6&period=30";
    }
}
