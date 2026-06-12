using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Tenancy;

/// <summary>
/// The HTTP half of the load-bearing tenancy pattern (§C.4). For an authenticated request it
/// resolves the org from the user's <c>org_id</c> claim, opens a single DB transaction, sets
/// <c>app.org_id</c> inside it with <c>set_config(..., is_local => true)</c>, runs the request, and
/// commits (rolls back on exception). The <c>SET LOCAL</c> dies with the transaction, so the next
/// request reusing this pooled connection starts with no context. Unauthenticated requests get
/// <b>no context</b> — RLS then returns nothing (fail closed); anonymous/auth endpoints still work
/// because Identity tables are identity-class, not RLS-protected.
/// </summary>
public sealed class OrgContextMiddleware(RequestDelegate next)
{
    /// <summary>Claim carrying the caller's org id; added at sign-in by WP-06.</summary>
    public const string OrgIdClaim = "org_id";

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext, AppDbContext db)
    {
        var orgClaim = context.User.FindFirst(OrgIdClaim)?.Value;
        if (context.User.Identity?.IsAuthenticated != true || !Guid.TryParse(orgClaim, out var orgId))
        {
            // No resolved org → no transaction → no app.org_id → RLS matches nothing.
            await next(context);
            return;
        }

        var ct = context.RequestAborted;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync(
                $"SELECT set_config('app.org_id', {orgId.ToString()}, true)", ct);
            tenantContext.OrgId = orgId;

            await next(context);

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
