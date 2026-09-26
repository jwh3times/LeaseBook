using System.Security.Claims;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Portal;

/// <summary>All operations require the caller's org transaction. No identity matching by email.</summary>
public sealed class ResidentAccessService(AppDbContext db, IOrgContext org, ISender sender, TimeProvider clock)
{
    public async Task<ResidentIdentity?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (org.OrgId is null || db.Database.CurrentTransaction is null) { return null; }
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            || !await db.Users.AnyAsync(u => u.Id == userId && u.OrgId == org.OrgId, ct))
        {
            return null;
        }

        var link = await db.Set<ResidentAccess>().AsNoTracking()
            .SingleOrDefaultAsync(l => l.UserId == userId && l.RevokedAt == null, ct);
        if (link is null) { return null; }
        var names = await sender.Query(new GetResidentNames([link.TenantId]), ct);
        return names.TryGetValue(link.TenantId, out var name) ? new(link.TenantId, name) : null;
    }

    public async Task GrantAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        RequireScope();
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.OrgId == org.OrgId, ct)
            || !(await sender.Query(new GetResidentNames([tenantId]), ct)).ContainsKey(tenantId))
        {
            throw new InvalidOperationException("Resident access requires a user and non-system tenant in the current organization.");
        }
        var existing = await db.Set<ResidentAccess>()
            .SingleOrDefaultAsync(l => l.UserId == userId && l.RevokedAt == null, ct);
        if (existing?.TenantId == tenantId) { return; }
        if (existing is not null)
        {
            throw new InvalidOperationException("Revoke the existing resident link before granting another.");
        }
        db.Add(new ResidentAccess { Id = UuidV7.NewId(), UserId = userId, TenantId = tenantId });
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
        var link = await db.Set<ResidentAccess>().SingleOrDefaultAsync(l => l.UserId == userId && l.RevokedAt == null, ct);
        if (link is null) { return; }
        link.RevokedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    private void RequireScope()
    {
        if (org.OrgId is null || db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Resident access requires an organization transaction.");
        }
    }
}

public sealed record ResidentIdentity(Guid TenantId, string DisplayName);
