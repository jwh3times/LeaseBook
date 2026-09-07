using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Tenancy;
using Microsoft.AspNetCore.Identity;

namespace LeaseBook.Web.Auth;

/// <summary>Account-security events carry actor attribution, never credentials or token values.</summary>
public sealed class AccountSecurityAudit(AppDbContext db, IActorContext actors, IOrgContext orgs)
{
    public async Task RecordAsync(AppUser user, string action, CancellationToken ct)
    {
        var actor = actors.Actor ?? throw new InvalidOperationException("Account security requires an actor.");
        if (orgs.OrgId == Guid.Empty || user.OrgId != orgs.OrgId)
        {
            throw new InvalidOperationException("Account security requires the user's organization scope.");
        }
        db.AuditEvents.Add(new AuditEvent
        {
            Id = UuidV7.NewId(),
            OccurredAt = DateTime.UtcNow,
            OrgId = user.OrgId,
            EntityType = "account-security",
            EntityId = user.Id,
            Action = action,
            ActorKind = actor.Kind,
            ActorUserId = actor.UserId,
            ActorProcess = actor.Process,
        });
        await db.SaveChangesAsync(ct);
    }

    internal static void RequireSuccess(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Account security update failed; no changes were committed.");
        }
    }
}
