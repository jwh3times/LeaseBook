using System.Security.Claims;
using LeaseBook.SharedKernel.Tenancy;

namespace LeaseBook.Web.Tenancy;

/// <summary>
/// The HTTP half of the load-bearing tenancy pattern (§C.4). For an authenticated request it
/// resolves the org from the user's <c>org_id</c> claim and runs the rest of the pipeline inside one
/// <see cref="OrgScopedExecutor"/> unit of work, so <c>app.org_id</c> is set with
/// <c>set_config(..., is_local => true)</c> and dies with the transaction that set it — the next
/// request reusing this pooled connection starts with no context. Unauthenticated requests get
/// <b>no context</b> — RLS then returns nothing (fail closed); anonymous/auth endpoints still work
/// because Identity tables are identity-class, not RLS-protected.
/// <para>
/// This middleware <i>calls</i> the executor rather than reimplementing it. What stays here is the
/// HTTP policy the executor has no business knowing: turning claims into an org id, and the
/// deliberate difference that a missing org means <b>no transaction at all</b> here, whereas it
/// means <b>throw</b> in the executor. Jobs and the CLI have no fail-closed fallback to degrade to,
/// so for them a missing org is a bug; for an anonymous request it is the normal case.
/// </para>
/// </summary>
public sealed class OrgContextMiddleware(RequestDelegate next)
{
    /// <summary>Claim carrying the caller's org id; added at sign-in by WP-06.</summary>
    public const string OrgIdClaim = "org_id";

    public async Task InvokeAsync(
        HttpContext context,
        ActorContext actorContext,
        OrgScopedExecutor executor)
    {
        var orgClaim = context.User.FindFirst(OrgIdClaim)?.Value;
        if (context.User.Identity?.IsAuthenticated != true || !Guid.TryParse(orgClaim, out var orgId))
        {
            // No resolved org → no transaction → no app.org_id → RLS matches nothing.
            await next(context);
            return;
        }

        await executor.RunAsync(orgId, async () =>
        {
            // The acting user (P52) — stamps journal_entries.created_by and audit_events.actor_user_id.
            if (Guid.TryParse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
            {
                actorContext.UserId = userId;
            }

            await next(context);
        }, context.RequestAborted);
    }
}
