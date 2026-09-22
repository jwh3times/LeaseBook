namespace LeaseBook.Web.Security;

/// <summary>The static security-header values applied to every response. CSP is enforced (not
/// report-only): nothing is deployed yet, so fixing violations now is cheapest. Tighten
/// <see cref="ContentSecurityPolicy"/> only after verifying the built SPA renders under it.</summary>
public static class SecurityHeaderOptions
{
    // Verified against the built SPA before lock (WP-5 §2). 'unsafe-inline' for style-src is the one
    // concession Vite's injected styles need; scripts stay strict.
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "img-src 'self' data:; " +
        "style-src 'self' 'unsafe-inline'; " +
        "script-src 'self'; " +
        "connect-src 'self'; " +
        "font-src 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "object-src 'none'";

    public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=()";

    public const string ReferrerPolicy = "no-referrer";

    /// <summary>Appended to the CSP only where HSTS is sent. Browsers apply it to localhost too, so in
    /// Development — plain <c>http://localhost</c> — it would rewrite the SPA's own assets to an https
    /// origin nothing serves.</summary>
    public const string UpgradeInsecureRequests = "upgrade-insecure-requests";

    /// <summary>The SPA opens no cross-origin windows and serves nothing meant for other sites.</summary>
    public const string CrossOriginOpenerPolicy = "same-origin";

    public const string CrossOriginResourcePolicy = "same-origin";
}
