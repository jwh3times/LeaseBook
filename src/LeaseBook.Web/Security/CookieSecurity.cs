using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.Hosting;

namespace LeaseBook.Web.Security;

/// <summary>
/// One source of truth for whether a cookie this application sets must carry <c>Secure</c>, and the
/// pipeline chokepoint that applies it.
/// <para>
/// The decision is derived from the <b>environment</b>, not from <c>Request.IsHttps</c>, and that
/// distinction is the whole point. <c>http://localhost</c> is genuinely plain HTTP in Development, so
/// forcing the flag there makes every cookie undeliverable and the inner loop unusable. Everywhere
/// else, TLS terminates at the edge and the origin is reached over plain HTTP, so the scheme Kestrel
/// observes says nothing about the scheme the browser used. <c>ForwardedHeaders</c> can restore that
/// signal, but it ships off until an operator names the ingress (ADR-041) and is configuration a
/// cookie flag should not silently depend on. The environment is the one input that is correct in
/// both topologies.
/// </para>
/// </summary>
public static class CookieSecurity
{
    /// <summary>The policy every cookie this application sets is expected to follow.</summary>
    public static CookieSecurePolicy PolicyFor(IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    }

    /// <summary>
    /// <see cref="PolicyFor"/> resolved against a request, for the one cookie written by hand onto
    /// <c>HttpResponse.Cookies</c> — <c>CookieOptions.Secure</c> is a bool and cannot carry a policy.
    /// </summary>
    public static bool IsSecureRequired(IWebHostEnvironment environment, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PolicyFor(environment) switch
        {
            CookieSecurePolicy.Always => true,
            CookieSecurePolicy.SameAsRequest => request.IsHttps,
            _ => false,
        };
    }

    /// <summary>
    /// Stamps <see cref="PolicyFor"/> onto every cookie that leaves this application, whoever writes
    /// it.
    /// <para>
    /// Per-call-site configuration covers only the cookies whose flags we own. It cannot reach a
    /// cookie minted by a framework scheme nobody configured — Identity registers
    /// <c>Identity.TwoFactorUserId</c> and <c>Identity.TwoFactorRememberMe</c> itself, and their
    /// cookie builders are never touched here — and it is the wrong altitude for a property that has
    /// to hold for the next cookie somebody adds, too. This is the chokepoint that makes the property
    /// structural rather than a convention each author must remember.
    /// </para>
    /// <para>
    /// Register it before anything downstream can write a cookie; it works by replacing the response
    /// cookie feature for the rest of the request, so a cookie written upstream of it is not covered.
    /// It is registered in every environment on purpose: the pipeline then has one shape, and the
    /// Development test suite exercises the same ordering the deployed host uses.
    /// </para>
    /// </summary>
    public static IApplicationBuilder UseLeaseBookCookiePolicy(
        this IApplicationBuilder app, IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseCookiePolicy(new CookiePolicyOptions
        {
            Secure = PolicyFor(environment),

            // Secure only. Both of the following are pinned to their no-op values rather than left
            // to a framework default, because either one silently breaks something real: raising
            // SameSite would change cross-site behaviour nobody asked for, and forcing HttpOnly
            // would make the XSRF-TOKEN cookie unreadable to the SPA that has to echo it back,
            // turning every unsafe request into an antiforgery rejection.
            MinimumSameSitePolicy = SameSiteMode.Unspecified,
            HttpOnly = HttpOnlyPolicy.None,
        });
    }
}
