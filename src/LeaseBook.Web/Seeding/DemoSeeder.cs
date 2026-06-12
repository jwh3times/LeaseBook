using System.Reflection;
using System.Text.Json;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Seeding;

/// <summary>
/// Provisions the demo org ("Tarheel Property Group") and its admin from the embedded golden dataset.
/// Idempotent — safe to re-run. Structured as ordered steps so later milestones extend it without
/// reshaping: a global step (org), an identity step (admin), then org-scoped steps run through
/// <see cref="OrgScopedExecutor"/> (the wrapper's first real consumer). Domains without a schema yet
/// (journal, directory, banking …) are explicit stubs below.
/// </summary>
public static class DemoSeeder
{
    /// <summary>Stable id so re-runs upsert rather than duplicate.</summary>
    public static readonly Guid DemoOrgId = new("01923000-0000-7000-8000-0000000d3110");

    public const string AdminEmail = "renee.calloway@tarheelpg.test";

    /// <summary>DEV ONLY documented seed password — real environments provision via Key Vault / invite.</summary>
    public const string AdminPassword = "Tarheel-Trust-2026!";

    private const string ProvisionAuditEntityType = "org-provisioned";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var data = LoadDataset();

        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // Roles must exist before the admin is assigned one (idempotent).
        await RoleSeeder.EnsureRolesAsync(sp, ct);

        // Step 1 (global-class): the org itself — it has no org_id, so no context is needed.
        await EnsureOrgAsync(sp.GetRequiredService<AppDbContext>(), data.Pm.Company, ct);

        // Step 2 (identity-class): the admin, bound to the org. MFA is left unenrolled (operator
        // self-enrolls at first login). Identity tables carry no RLS, so again no org context.
        await EnsureAdminAsync(sp.GetRequiredService<UserManager<AppUser>>(), data.Pm.User.Name, ct);

        // Step 3 (org-scoped): runs inside the OrgScopedExecutor unit of work — app.org_id is set,
        // so the write passes RLS WITH CHECK. Records a single provisioning event in the audit log.
        // M1 adds journal seed steps here; M2 adds directory (owners/properties/tenants); etc.
        var executor = sp.GetRequiredService<OrgScopedExecutor>();
        var db = sp.GetRequiredService<AppDbContext>();
        await executor.RunAsync(DemoOrgId, async () =>
        {
            var alreadyProvisioned = await db.AuditEvents
                .AnyAsync(e => e.EntityType == ProvisionAuditEntityType, ct);
            if (!alreadyProvisioned)
            {
                db.AuditEvents.Add(new AuditEvent
                {
                    Id = UuidV7.NewId(),
                    EntityType = ProvisionAuditEntityType,
                    EntityId = DemoOrgId,
                    Action = "seed",
                    After = JsonSerializer.Serialize(new { data.Pm.Company, data.Pm.City }),
                    OccurredAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }
        }, ct);
    }

    private static async Task EnsureOrgAsync(AppDbContext db, string company, CancellationToken ct)
    {
        if (await db.Orgs.AnyAsync(o => o.Id == DemoOrgId, ct))
        {
            return;
        }

        db.Orgs.Add(new Org { Id = DemoOrgId, Name = company });
        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureAdminAsync(UserManager<AppUser> userManager, string displayName, CancellationToken ct)
    {
        if (await userManager.FindByEmailAsync(AdminEmail) is not null)
        {
            return;
        }

        var admin = new AppUser
        {
            Id = UuidV7.NewId(),
            UserName = AdminEmail,
            Email = AdminEmail,
            EmailConfirmed = true,
            OrgId = DemoOrgId,
            DisplayName = displayName,
        };

        var created = await userManager.CreateAsync(admin, AdminPassword);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to seed the demo admin: " + string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        await userManager.AddToRoleAsync(admin, Roles.PMAdmin);
    }

    private static DemoOrgSeed LoadDataset()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("demo-org.json", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        return JsonSerializer.Deserialize<DemoOrgSeed>(stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    // Only the fields M0 consumes are modeled; the rest of the dataset seeds in later milestones.
    private sealed record DemoOrgSeed(PmInfo Pm);

    private sealed record PmInfo(string Company, string City, PmUser User);

    private sealed record PmUser(string Name, string Role, string Initials);
}
