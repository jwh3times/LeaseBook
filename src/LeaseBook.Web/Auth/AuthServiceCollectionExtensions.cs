using FluentValidation;
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

        // Auth request validators (P23) — executed by the ValidationEndpointFilter.
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
            options.Events.OnRedirectToLogin = ApiAwareStatus(StatusCodes.Status401Unauthorized);
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
}
