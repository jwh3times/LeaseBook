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
            LoginRequest request, SignInManager<AppUser> signInManager, UserManager<AppUser> userManager) =>
        {
            var user = await userManager.FindByEmailAsync(request.Email);
            if (user is not null)
            {
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
            }

            // Generic message for bad credentials, lockout, and unknown email alike.
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid credentials.");
        })
        .AddEndpointFilter<ValidationEndpointFilter<LoginRequest>>()
        .Produces<LoginResponse>()
        .AllowAnonymous()
        .RequireRateLimiting("auth");

        group.MapPost("/mfa", async (MfaRequest request, SignInManager<AppUser> signInManager) =>
        {
            var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user is null || !string.Equals(request.MfaToken, user.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid credentials.");
            }

            var result = await signInManager.TwoFactorAuthenticatorSignInAsync(
                request.Code, isPersistent: false, rememberClient: false);
            return result.Succeeded
                ? Results.Ok(new LoginResponse(LoginStatus.Ok, null))
                : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid code.");
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

        group.MapGet("/me", async (HttpContext http, UserManager<AppUser> userManager, AppDbContext db) =>
        {
            var user = await userManager.GetUserAsync(http.User);
            if (user is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Not authenticated.");
            }

            var roles = await userManager.GetRolesAsync(user);
            var orgName = await db.Orgs
                .Where(o => o.Id == user.OrgId)
                .Select(o => o.Name)
                .FirstOrDefaultAsync(http.RequestAborted);

            return Results.Ok(new MeResponse(
                user.Id, user.DisplayName, user.Email, roles.FirstOrDefault(), user.OrgId, orgName));
        })
        .Produces<MeResponse>()
        .RequireAuthorization(AuthPolicies.AuthenticatedMfaExempt);

        group.MapPost("/mfa/enroll", async (HttpContext http, UserManager<AppUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(http.User);
            if (user is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Not authenticated.");
            }

            var key = await userManager.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrEmpty(key))
            {
                await userManager.ResetAuthenticatorKeyAsync(user);
                key = await userManager.GetAuthenticatorKeyAsync(user);
            }

            var account = user.Email ?? user.UserName ?? user.Id.ToString();
            return Results.Ok(new EnrollResponse(BuildOtpauthUri("LeaseBook", account, key!), key!));
        })
        .Produces<EnrollResponse>()
        .RequireAuthorization(AuthPolicies.AuthenticatedMfaExempt);

        group.MapPost("/mfa/enroll/confirm", async (
            ConfirmMfaRequest request, HttpContext http, UserManager<AppUser> userManager, SignInManager<AppUser> signInManager) =>
        {
            var user = await userManager.GetUserAsync(http.User);
            if (user is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Not authenticated.");
            }

            var valid = await userManager.VerifyTwoFactorTokenAsync(
                user, userManager.Options.Tokens.AuthenticatorTokenProvider, request.Code);
            if (!valid)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid code.");
            }

            await userManager.SetTwoFactorEnabledAsync(user, true);
            await signInManager.RefreshSignInAsync(user); // re-issue the cookie so mfa_enrolled flips to true
            return Results.NoContent();
        })
        .AddEndpointFilter<ValidationEndpointFilter<ConfirmMfaRequest>>()
        .RequireAuthorization(AuthPolicies.AuthenticatedMfaExempt);
    }

    private static string BuildOtpauthUri(string issuer, string account, string secret)
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        var encodedIssuer = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={secret}&issuer={encodedIssuer}&digits=6&period=30";
    }
}
