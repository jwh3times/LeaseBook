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
}
