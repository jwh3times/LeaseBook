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
        // Registered via OnStarting rather than assigned directly: when a downstream handler throws,
        // ExceptionHandlerMiddleware clears the response (wiping any headers already set on it) before
        // invoking the app's registered IExceptionHandlers (ValidationExceptionHandler,
        // AccountingExceptionHandler), which write the response body directly without re-entering this
        // middleware. An OnStarting callback fires immediately before the response is actually sent to
        // the client and survives that clear, so it lands on both normal and exception-handler-driven
        // (400/409/422) responses alike.
        context.Response.OnStarting(() =>
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

            return Task.CompletedTask;
        });

        await next(context);
    }
}
