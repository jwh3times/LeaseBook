using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Auth;

/// <summary>Operator-only account bootstrap and recovery. Never registered as an HTTP endpoint.</summary>
public sealed class AccountAdministration(
    AppDbContext db, UserManager<AppUser> users, OrgScopedExecutor executor, AccountSecurityAudit audit)
{
    public async Task<Guid> CreateAdminAsync(string orgName, string email, string name, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orgName) || orgName.Length > 200 || string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            throw new ArgumentException("Organization and display names are required (maximum 200 characters).");
        }
        var orgId = UuidV7.NewId();
        await executor.RunAsSystemAsync(orgId, "accounts:create-admin", async () =>
        {
            db.Orgs.Add(new Org { Id = orgId, Name = orgName });
            await db.SaveChangesAsync(ct);
            var user = new AppUser
            {
                Id = UuidV7.NewId(),
                OrgId = orgId,
                UserName = email,
                Email = email,
                DisplayName = name,
                EmailConfirmed = true,
            };
            AccountSecurityAudit.RequireSuccess(await users.CreateAsync(user, password));
            AccountSecurityAudit.RequireSuccess(await users.AddToRoleAsync(user, Roles.PMAdmin));
            await audit.RecordAsync(user, "admin-created", ct);
        }, ct);
        return orgId;
    }

    public Task ResetMfaAsync(Guid orgId, string email, CancellationToken ct) =>
        executor.RunAsSystemAsync(orgId, "accounts:reset-mfa", async () =>
        {
            var normalized = users.NormalizeEmail(email);
            var user = await users.Users.SingleOrDefaultAsync(u => u.OrgId == orgId && u.NormalizedEmail == normalized, ct)
                ?? throw new InvalidOperationException("Account not found in the specified organization.");
            AccountSecurityAudit.RequireSuccess(await users.SetTwoFactorEnabledAsync(user, false));
            _ = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0)
                ?? throw new InvalidOperationException("Could not clear recovery codes.");
            AccountSecurityAudit.RequireSuccess(await users.ResetAuthenticatorKeyAsync(user));
            AccountSecurityAudit.RequireSuccess(await users.UpdateSecurityStampAsync(user));
            await audit.RecordAsync(user, "mfa-reset", ct);
        }, ct);
}
