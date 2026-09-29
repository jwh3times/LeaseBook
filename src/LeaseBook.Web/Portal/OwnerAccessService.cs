using System.Security.Claims;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Portal;

/// <summary>
/// Provisions, revokes and resolves owner-portal links. All operations require the caller's org
/// transaction. There is no identity matching by email and no browser-supplied owner id: the owner is
/// whatever the caller's active link names, read afresh each time.
/// </summary>
public sealed class OwnerAccessService(AppDbContext db, IOrgContext org, ISender sender, TimeProvider clock)
{
    public async Task<OwnerIdentity?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (org.OrgId is null || db.Database.CurrentTransaction is null) { return null; }
        // asp_net_users is RLS-exempt, so the org match is explicit here.
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            || !await db.Users.AnyAsync(u => u.Id == userId && u.OrgId == org.OrgId, ct))
        {
            return null;
        }

        var link = await db.Set<OwnerAccess>().AsNoTracking()
            .SingleOrDefaultAsync(l => l.UserId == userId && l.RevokedAt == null, ct);
        if (link is null) { return null; }
        var names = await sender.Query(new GetOwnerNames([link.OwnerId]), ct);
        return names.TryGetValue(link.OwnerId, out var name) ? new(link.OwnerId, name) : null;
    }

    public async Task GrantAsync(Guid userId, Guid ownerId, CancellationToken ct)
    {
        RequireScope();
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.OrgId == org.OrgId, ct)
            || !(await sender.Query(new GetOwnerNames([ownerId]), ct)).ContainsKey(ownerId))
        {
            throw new InvalidOperationException("Owner access requires a user and non-system owner in the current organization.");
        }
        var existing = await db.Set<OwnerAccess>()
            .SingleOrDefaultAsync(l => l.UserId == userId && l.RevokedAt == null, ct);
        if (existing?.OwnerId == ownerId) { return; }
        if (existing is not null)
        {
            throw new InvalidOperationException("Revoke the existing owner link before granting another.");
        }
        db.Add(new OwnerAccess { Id = UuidV7.NewId(), UserId = userId, OwnerId = ownerId });
        // AppDbContext records the link change with the ambient actor; no credentials are stored here.
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeAsync(Guid userId, CancellationToken ct)
    {
        RequireScope();
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.OrgId == org.OrgId, ct))
        {
            throw new InvalidOperationException("Account not found in the current organization.");
        }
        var link = await db.Set<OwnerAccess>().SingleOrDefaultAsync(l => l.UserId == userId && l.RevokedAt == null, ct);
        if (link is null) { return; }
        link.RevokedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    private void RequireScope()
    {
        if (org.OrgId is null || db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Owner access requires an organization transaction.");
        }
    }
}

/// <summary>The owner a portal request reads, resolved for that request only.</summary>
public sealed record OwnerIdentity(Guid OwnerId, string DisplayName);
