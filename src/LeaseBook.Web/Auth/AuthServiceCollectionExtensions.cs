using FluentValidation;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;

namespace LeaseBook.Web.Auth;

/// <summary>
/// Registers ASP.NET Core Identity (P12): EF stores on <see cref="AppDbContext"/>, the SPA cookie,
/// antiforgery (cookie-to-header), deny-by-default authorization, and the org-claim factory.
/// </summary>
public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddLeaseBookIdentity(
        this IServiceCollection services, IWebHostEnvironment environment)
    {
        // http://localhost is plain HTTP in dev; every other environment terminates TLS at the edge.
        var securePolicy = environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;

        services
            .AddIdentity<AppUser, IdentityRole<Guid>>(options =>
            {
                options.User.RequireUniqueEmail = true;
                // Operator accounts, not consumer signups — a stronger password floor is appropriate.
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                // Users are provisioned (WP-09 seeder); there is no self-signup confirmation flow.
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        // Sign-in mints the org_id claim the tenancy middleware consumes.
        services.AddScoped<IUserClaimsPrincipalFactory<AppUser>, AppUserClaimsPrincipalFactory>();

        // Singleton: it mints one decoy hash at construction, and that cost should be paid once at
        // startup rather than on the request that needs the timing to match.
        services.AddSingleton<PasswordTimingEqualizer>();

        // Auth request validators (P23) — executed by the ValidationEndpointFilter.
        services.AddScoped<AccountSecurityAudit>();
        services.AddScoped<AccountAdministration>();
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        services.AddScoped<IValidator<RecoveryLoginRequest>, RecoveryLoginRequestValidator>();
        services.AddScoped<IValidator<ChangePasswordRequest>, ChangePasswordRequestValidator>();
        services.AddScoped<IValidator<LoginRequest>, LoginRequestValidator>();
        services.AddScoped<IValidator<MfaRequest>, MfaRequestValidator>();
        services.AddScoped<IValidator<ConfirmMfaRequest>, ConfirmMfaRequestValidator>();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "LeaseBook.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = securePolicy;
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            // SPA over /api expects status codes, not redirects to a login page.
            options.Events.OnRedirectToLogin = ApiAwareProblem(
                StatusCodes.Status401Unauthorized, code: "not_authenticated", detail: "Not authenticated.");
            // Deliberately still a bare status: AuthorizationMatrixTests asserts a role denial is
            // *not* problem+json, which is what pins the MFA result handler's problem response to
            // MFA alone. Do not "finish the job" here without moving that test's meaning first.
            options.Events.OnRedirectToAccessDenied = ApiAwareStatus(StatusCodes.Status403Forbidden);
        });

        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-XSRF-TOKEN";
            options.Cookie.Name = "LeaseBook.Antiforgery";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = securePolicy;
        });

        var mfaEnrolled = new MfaEnrolledRequirement();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().AddRequirements(mfaEnrolled).Build())
            .AddPolicy(AuthPolicies.RequirePMAdmin, policy => policy
                .RequireRole(Roles.PMAdmin).AddRequirements(mfaEnrolled))
            .AddPolicy(AuthPolicies.RequirePMStaff, policy => policy
                .RequireRole(Roles.PMAdmin, Roles.PMStaff).AddRequirements(mfaEnrolled))
            .AddPolicy(AuthPolicies.AuthenticatedMfaExempt, policy => policy
                .RequireAuthenticatedUser());

        return services;
    }

    private static Func<RedirectContext<CookieAuthenticationOptions>, Task> ApiAwareStatus(int statusCode) =>
        context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = statusCode;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };

    /// <summary>
    /// The <see cref="ApiAwareStatus"/> shape, but writing the ADR-025 problem body rather than a
    /// bare status.
    ///
    /// An expired cookie is by far the most common error any signed-in operator meets, and it was
    /// the one error response in the product carrying neither a <c>code</c> nor a
    /// <c>correlationId</c> — <c>ErrorContractTests</c> could not see the gap, because it scans for
    /// direct <c>Results.Problem</c> calls and this path never made one. The SPA was left inferring
    /// "signed out" from a body-less 401 (#357).
    ///
    /// Writing a body means the response has started once the challenge returns, where the bare
    /// status write was idempotent. That is safe as long as one response takes at most one
    /// challenge: <c>AuthorizationMiddlewareResultHandler</c> issues one per scheme when a policy
    /// declares <c>AuthenticationSchemes</c>, and none of the policies above declare any. A second
    /// challenge on the same response would set a status on a started response and surface as a 500,
    /// so if a policy ever names two schemes, revisit this rather than the symptom.
    /// </summary>
    private static Func<RedirectContext<CookieAuthenticationOptions>, Task> ApiAwareProblem(
        int statusCode, string code, string detail) =>
        async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                await ProblemResults
                    .TypedProblem(context.HttpContext, code, detail, statusCode)
                    .ExecuteAsync(context.HttpContext);
                return;
            }

            context.Response.Redirect(context.RedirectUri);
        };
}
