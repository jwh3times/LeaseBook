using System.Net.Mail;
using System.Security.Claims;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Settings;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Portal;

/// <summary>Host-owned identity workflow. Directory selection stays explicit; email never selects a financial record.</summary>
public sealed class PortalEnrollmentService(AppDbContext db, IOrgContext org, ISender sender,
    TimeProvider clock, IWebHostEnvironment environment, IServiceScopeFactory scopes,
    PortalInvitationProof proof, IPortalInvitationDelivery delivery, UserManager<AppUser> users,
    ResidentAccessService residents, OwnerAccessService owners)
{
    private AppDbContext Db => db;
    private TimeProvider Clock => clock;
    private UserManager<AppUser> Users => users;
    private ResidentAccessService Residents => residents;
    private OwnerAccessService Owners => owners;

    public async Task<PortalAccessResponse> ReadAsync(string persona, Guid targetId, ClaimsPrincipal principal, CancellationToken ct)
    {
        await TargetAsync(persona, targetId, ct);
        var invitations = await db.Set<PortalInvitation>().AsNoTracking()
            .Where(i => i.Persona == persona && (i.TenantId == targetId || i.OwnerId == targetId))
            .OrderByDescending(i => i.CreatedAt).ToListAsync(ct);
        var ids = persona == "tenant"
            ? await db.Set<ResidentAccess>().Where(l => l.TenantId == targetId && l.RevokedAt == null).Select(l => l.UserId).ToListAsync(ct)
            : await db.Set<OwnerAccess>().Where(l => l.OwnerId == targetId && l.RevokedAt == null).Select(l => l.UserId).ToListAsync(ct);
        var users = await db.Users.Where(u => u.OrgId == org.OrgId && ids.Contains(u.Id))
            .Select(u => new PortalAccessUser(u.Id, u.Email!, u.DisplayName)).ToListAsync(ct);
        return new(await CanManageAsync(principal, ct), invitations.Select(Summary).ToArray(), users);
    }

    public async Task<PortalInvitationSummary> InviteAsync(string persona, Guid targetId, string email, ClaimsPrincipal principal, CancellationToken ct)
    {
        await PortalInvitationDispatcher.LockAsync(db, org.OrgId!.Value, ct);
        await RequireManagementAsync(principal, ct);
        await TargetAsync(persona, targetId, ct);
        email = email?.Trim() ?? "";
        if (email.Length > 254 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
        { throw new PortalEnrollmentException("invalid_email", "Enter a valid email address."); }
        var row = new PortalInvitation
        {
            Id = UuidV7.NewId(),
            OrgId = org.OrgId!.Value,
            Persona = persona,
            TenantId = persona == "tenant" ? targetId : null,
            OwnerId = persona == "owner" ? targetId : null,
            Email = email,
            NormalizedEmail = users.NormalizeEmail(email)!,
            CreatedAt = clock.GetUtcNow(),
            ExpiresAt = clock.GetUtcNow().AddHours(72),
            CreatedByUserId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Status = "pending",
            DeliveryStatus = "pending",
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);
        return Summary(row);
    }

    public async Task<PortalInvitationSummary> ReplaceAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var row = await ManagedPendingAsync(id, principal, ct);
        await TargetAsync(row.Persona, Target(row), ct);
        var replacement = new PortalInvitation
        {
            Id = UuidV7.NewId(),
            OrgId = row.OrgId,
            Persona = row.Persona,
            TenantId = row.TenantId,
            OwnerId = row.OwnerId,
            Email = row.Email,
            NormalizedEmail = row.NormalizedEmail,
            CreatedAt = clock.GetUtcNow(),
            ExpiresAt = clock.GetUtcNow().AddHours(72),
            CreatedByUserId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!),
        };
        row.Status = "replaced";
        db.Add(replacement);
        await db.SaveChangesAsync(ct);
        return Summary(replacement);
    }

    public async Task<PortalAccessUpdated> CancelAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var row = await ManagedPendingAsync(id, principal, ct);
        row.Status = "cancelled";
        await db.SaveChangesAsync(ct);
        return new();
    }
    public async Task<PortalAccessUpdated> RetryAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var row = await ManagedPendingAsync(id, principal, ct);
        if (row.ExpiresAt <= clock.GetUtcNow() || row.DeliveryStatus != "failed") { throw Invalid(); }
        row.DeliveryStatus = "pending";
        row.DeliveryError = null;
        await db.SaveChangesAsync(ct);
        return new();
    }
    public async Task<PortalAccessUpdated> RevokeAsync(string persona, Guid targetId, Guid userId, ClaimsPrincipal principal, CancellationToken ct)
    {
        await PortalInvitationDispatcher.LockAsync(db, org.OrgId!.Value, ct);
        await RequireManagementAsync(principal, ct);
        await TargetAsync(persona, targetId, ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.OrgId == org.OrgId, ct);
        var linked = persona == "tenant"
            ? await db.Set<ResidentAccess>().AnyAsync(l => l.UserId == userId && l.TenantId == targetId && l.RevokedAt == null, ct)
            : await db.Set<OwnerAccess>().AnyAsync(l => l.UserId == userId && l.OwnerId == targetId && l.RevokedAt == null, ct);
        if (user is null || !linked) { throw Invalid(); }
        var pending = await db.Set<PortalInvitation>().Where(i => i.Persona == persona && (i.TenantId == targetId || i.OwnerId == targetId)
            && i.NormalizedEmail == user.NormalizedEmail && i.Status == "pending").ToListAsync(ct);
        foreach (var row in pending) { row.Status = "cancelled"; }
        if (persona == "tenant") { await residents.RevokeAsync(userId, ct); }
        else { await owners.RevokeAsync(userId, ct); }
        await db.SaveChangesAsync(ct);
        return new();
    }

    private async Task<PortalInvitation> ManagedPendingAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        await PortalInvitationDispatcher.LockAsync(db, org.OrgId!.Value, ct);
        await RequireManagementAsync(principal, ct);
        var row = await db.Set<PortalInvitation>().SingleOrDefaultAsync(i => i.Id == id, ct);
        return row is not null && row.Status == "pending" ? row : throw Invalid();
    }
    public async Task<IReadOnlyList<PortalTestMessage>> InboxAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        await RequireManagementAsync(principal, ct);
        var ids = await db.Set<PortalInvitation>().Where(i => i.Status == "pending" && i.ExpiresAt > clock.GetUtcNow())
            .Select(i => i.Id).ToListAsync(ct);
        var messages = new List<PortalTestMessage>();
        foreach (var id in ids)
        {
            if (await delivery.ReadAsync(org.OrgId!.Value, id, ct) is { } message) { messages.Add(message); }
        }
        return messages;
    }

    public Task<PortalEnrollmentInspection> InspectAsync(string token, ClaimsPrincipal principal, CancellationToken ct) =>
        WithProofAsync(token, async (service, row) =>
        {
            var name = await service.TargetAsync(row.Persona, Target(row), ct);
            var account = await service.CompatibleAccountAsync(row, principal, false, ct);
            return new PortalEnrollmentInspection(row.Email, name, row.Persona,
                account is not null && principal.Identity?.IsAuthenticated != true, account is null);
        }, ct);

    public Task<PortalEnrollmentAccepted> AcceptAsync(AcceptPortalInvitation body, ClaimsPrincipal principal, CancellationToken ct) =>
        WithProofAsync(body.Token, async (service, row) =>
        {
            await service.TargetAsync(row.Persona, Target(row), ct);
            var account = await service.CompatibleAccountAsync(row, principal, true, ct);
            if (account is null)
            {
                if (string.IsNullOrWhiteSpace(body.DisplayName) || body.DisplayName.Trim().Length > 120)
                { throw new PortalEnrollmentException("invalid_display_name", "Enter a name of no more than 120 characters."); }
                if (string.IsNullOrEmpty(body.Password) || body.Password.Length > 256)
                { throw new PortalEnrollmentException("invalid_password", "Use a password of 12 to 256 characters with uppercase, lowercase, number and symbol."); }
                account = new AppUser
                {
                    Id = UuidV7.NewId(),
                    OrgId = row.OrgId,
                    UserName = row.Email,
                    Email = row.Email,
                    EmailConfirmed = true,
                    DisplayName = body.DisplayName.Trim()
                };
                var created = await service.Users.CreateAsync(account, body.Password);
                if (!created.Succeeded)
                { throw new PortalEnrollmentException("invalid_password", "Use a password of 12 to 256 characters with uppercase, lowercase, number and symbol."); }
                if (!(await service.Users.AddToRoleAsync(account, Role(row.Persona))).Succeeded) { throw Invalid(); }
            }
            else
            {
                // The mailbox proof confirms only email ownership; it never changes existing credentials or MFA.
                account.EmailConfirmed = true;
                if (!(await service.Users.UpdateAsync(account)).Succeeded) { throw Invalid(); }
            }
            if (row.Persona == "tenant") { await service.Residents.GrantAsync(account.Id, Target(row), ct); }
            else { await service.Owners.GrantAsync(account.Id, Target(row), ct); }
            row.Status = "accepted";
            row.AcceptedAt = service.Clock.GetUtcNow();
            row.AcceptedByUserId = account.Id;
            await service.Db.SaveChangesAsync(ct);
            return new PortalEnrollmentAccepted(row.Persona);
        }, ct);

    // A fresh scope avoids nesting the authenticated browser's request transaction. Exceptions escape
    // this executor before the endpoint maps them, so Identity, link, audit and consumption roll back together.
    private async Task<T> WithProofAsync<T>(string token, Func<PortalEnrollmentService, PortalInvitation, Task<T>> work, CancellationToken ct)
    {
        if (!environment.IsDevelopment()) { throw Invalid(); }
        var verified = proof.Read(token);
        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<PortalEnrollmentService>();
        return await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(verified.OrgId,
            "portal-invitation-redemption", async () =>
            {
                await PortalInvitationDispatcher.LockAsync(service.Db, verified.OrgId, ct);
                var row = await service.Db.Set<PortalInvitation>().SingleOrDefaultAsync(i => i.Id == verified.Id, ct);
                if (row is null || row.Status != "pending" || row.ExpiresAt <= service.Clock.GetUtcNow()) { throw Invalid(); }
                return await work(service, row);
            }, ct);
    }

    private async Task<AppUser?> CompatibleAccountAsync(PortalInvitation row, ClaimsPrincipal principal, bool accepting, CancellationToken ct)
    {
        // Identity is RLS-exempt: materialize only the proof's organization. The boolean global
        // uniqueness check refuses conflicting logins without loading or exposing their account data.
        var account = await db.Users.SingleOrDefaultAsync(u => u.OrgId == row.OrgId && u.NormalizedEmail == row.NormalizedEmail, ct);
        if (account is null && await db.Users.AnyAsync(u => u.NormalizedEmail == row.NormalizedEmail, ct)) { throw Invalid(); }
        var signedIn = principal.Identity?.IsAuthenticated == true;
        if (signedIn && (account is null || principal.FindFirstValue(ClaimTypes.NameIdentifier) != account.Id.ToString()))
        { throw new PortalEnrollmentException("incompatible_session", "Sign out before opening this invitation.", 409); }
        if (account is null) { return null; }
        var roles = await users.GetRolesAsync(account);
        if (account.OrgId != row.OrgId || account.NormalizedEmail != row.NormalizedEmail || roles.Count != 1 || roles[0] != Role(row.Persona))
        { throw Invalid(); }
        var conflictingHistory = row.Persona == "tenant"
            ? await db.Set<ResidentAccess>().AnyAsync(l => l.UserId == account.Id && l.TenantId != row.TenantId, ct)
            : await db.Set<OwnerAccess>().AnyAsync(l => l.UserId == account.Id && l.OwnerId != row.OwnerId, ct);
        if (conflictingHistory) { throw Invalid(); }
        if (accepting && !signedIn)
        { throw new PortalEnrollmentException("invitation_sign_in_required", "Sign in to your existing account before accepting this invitation.", 401); }
        if (signedIn && (!principal.IsInRole(Role(row.Persona))
            || principal.IsInRole(Roles.PMAdmin) || principal.IsInRole(Roles.PMStaff)
            || principal.IsInRole(row.Persona == "tenant" ? Roles.Owner : Roles.Tenant))) { throw Invalid(); }
        return account;
    }

    private static Guid Target(PortalInvitation row) => row.TenantId ?? row.OwnerId!.Value;
    private static string Role(string persona) => persona == "tenant" ? Roles.Tenant : Roles.Owner;

    private PortalInvitationSummary Summary(PortalInvitation row) => new(row.Id, row.Email,
        row.Status == "pending" && row.ExpiresAt <= clock.GetUtcNow() ? "expired" : row.Status, row.ExpiresAt, row.DeliveryStatus);

    private async Task<bool> CanManageAsync(ClaimsPrincipal principal, CancellationToken ct) => environment.IsDevelopment()
        && (principal.IsInRole(Roles.PMAdmin) || (await sender.Query(new GetOrgSettings(false), ct)).StaffCanManagePortalAccess);

    private async Task RequireManagementAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!await CanManageAsync(principal, ct))
        { throw new PortalEnrollmentException("portal_access_management_denied", "Portal access management is unavailable for this account.", 403); }
    }

    private async Task<string> TargetAsync(string persona, Guid targetId, CancellationToken ct)
    {
        var names = persona switch
        {
            "tenant" => await sender.Query(new GetResidentNames([targetId]), ct),
            "owner" => await sender.Query(new GetOwnerNames([targetId]), ct),
            _ => throw Invalid(),
        };
        return names.TryGetValue(targetId, out var name) ? name : throw Invalid();
    }

    internal static PortalEnrollmentException Invalid() => new("invalid_invitation", "This invitation is unavailable or has expired.");
}
