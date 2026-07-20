using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Antiforgery;

namespace LeaseBook.Web.Auth;

/// <summary>
/// Enforces cookie-to-header XSRF (P12) on unsafe <c>/api</c> requests: the SPA reads the
/// <c>XSRF-TOKEN</c> cookie (refreshed via <c>GET /api/auth/csrf</c>) and echoes it in the
/// <c>X-XSRF-TOKEN</c> header. Safe (idempotent) methods are exempt. Runs before the org-context
/// middleware so a rejected request never opens a database transaction.
/// </summary>
public sealed class ApiAntiforgeryMiddleware(RequestDelegate next, IAntiforgery antiforgery)
{
    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api") && !SafeMethods.Contains(context.Request.Method))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                await ProblemResults.Problem(
                        context,
                        code: "antiforgery_rejected",
                        detail: "Invalid or missing antiforgery token.",
                        status: StatusCodes.Status400BadRequest)
                    .ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }
}
