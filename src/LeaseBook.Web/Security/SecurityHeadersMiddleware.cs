using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;

namespace LeaseBook.Web.Security;

/// <summary>Adds fixed security headers to every response. Hand-rolled (no NuGet dependency),
/// registered early so it covers the SPA and /api alike. HSTS is Production-only (the edge
/// terminates TLS there; localhost is plain HTTP).</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment environment)
{
    private readonly bool _emitHsts = !environment.IsDevelopment();

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = SecurityHeaderOptions.ReferrerPolicy;
        headers["Permissions-Policy"] = SecurityHeaderOptions.PermissionsPolicy;
        headers["Content-Security-Policy"] = SecurityHeaderOptions.ContentSecurityPolicy;
        if (_emitHsts)
        {
            headers[HeaderNames.StrictTransportSecurity] = "max-age=31536000; includeSubDomains";
        }

        await next(context);
    }
}
