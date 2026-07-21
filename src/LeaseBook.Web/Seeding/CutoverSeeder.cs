using System.Text.Json;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using AccountingBankPurpose = LeaseBook.Modules.Accounting.Contracts.BankPurpose;
using DirectoryBankPurpose = LeaseBook.Modules.Directory.Domain.BankPurpose;

namespace LeaseBook.Web.Seeding;

/// <summary>
/// Provisions the synthetic "cutover" org used by the M7 onboarding e2e and fixture verification.
///
/// <para>
/// The cutover org contains an empty operational org — chart of accounts + trust bank accounts +
/// a PMAdmin login — but <b>NO journal data</b>, so the dashboard redirects to the
/// onboarding wizard. This is the complement of the demo org: a clean slate for testing
/// the import-first onboarding path.
/// </para>
///
/// <para>
/// Bank accounts provisioned (names must match <c>seed/cutover-fixture/bank_balances.csv</c>):
/// <list type="bullet">
///   <item>"Cutover Operating Trust" — Trust purpose (for owner balances).</item>
///   <item>"Cutover Deposit Trust" — Deposit purpose (for deposit liabilities).</item>
/// </list>
/// </para>
///
/// <para>
/// Fixture tie-out (see <c>seed/cutover-fixture/</c>):
///   Operating Trust book = $8,700.00 = $8,500.00 owner equity ($5,000 O-C1 + $3,500 O-C2) + $200.00
///   held PM fees (<c>held_pm_fees.csv</c>) ✓
///   Deposit Trust book = $4,500.00 = $1,500 + $1,250 + $1,750 = Σ deposit liabilities ✓
///   Cash == Accrual (happy path): no accrual-delta line → MigrationClearing nets to $0.00 in both bases.
///   The e2e correction leg deliberately understates O-C1 at $4,950.00 in <c>owner_balances.csv</c>;
///   the dedicated correction CSV, <c>owner_balances_corrected.csv</c>, supersedes it back to
///   $5,000.00 before the tie above is checked.
/// </para>
///
/// <para>
/// Idempotent — safe to re-run. Does NOT touch <see cref="DemoJournalSeed"/> or the demo golden.
/// </para>
/// </summary>
public static class CutoverSeeder
{
    /// <summary>Stable id for the cutover org — deterministic so re-runs upsert rather than duplicate.</summary>
    public static readonly Guid CutoverOrgId = new("01923000-0000-7000-8000-0000000c7700");

    public const string AdminEmail = "admin@cutover.test";
    public const string AdminPassword = "Cutover-Trust-2026!";

    /// <summary>Stable bank-account ids so the chart-of-accounts provisioning is idempotent.</summary>
    public static readonly Guid OperatingTrustId = new("01923000-0000-7000-8000-0000c7ba0001");
    public static readonly Guid DepositTrustId = new("01923000-0000-7000-8000-0000c7ba0002");

    private const string ProvisionAuditEntityType = "org-provisioned";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        // Refuse to provision the well-known cutover admin credential in Production (account-takeover risk).
        SeederGuard.RequireNonProduction(services);

        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // Roles must exist before the admin is assigned one.
        await RoleSeeder.EnsureRolesAsync(sp, ct);

        // Step 1 (global): the org itself.
        await EnsureOrgAsync(sp.GetRequiredService<AppDbContext>(), ct);

        // Step 2 (identity): the admin user.
        await EnsureAdminAsync(sp.GetRequiredService<UserManager<AppUser>>(), ct);

        // Step 3 (org-scoped): bank accounts + chart of accounts (no journal data).
        var executor = sp.GetRequiredService<OrgScopedExecutor>();
        var db = sp.GetRequiredService<AppDbContext>();
        await executor.RunAsync(CutoverOrgId, async () =>
        {
            await EnsureOrgContentAsync(db, sp, ct);
        }, ct);
    }

    private static async Task EnsureOrgAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Orgs.AnyAsync(o => o.Id == CutoverOrgId, ct))
        {
            return;
        }

        db.Orgs.Add(new Org { Id = CutoverOrgId, Name = "Cutover Demo PM" });
        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureAdminAsync(UserManager<AppUser> userManager, CancellationToken ct)
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
            OrgId = CutoverOrgId,
            DisplayName = "Cutover Admin",
        };

        var created = await userManager.CreateAsync(admin, AdminPassword);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to seed the cutover admin: " + string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        await userManager.AddToRoleAsync(admin, Roles.PMAdmin);
    }

    /// <summary>
    /// Provisions org settings, bank accounts, and the chart of accounts for the cutover org.
    /// No journal data is posted — the org is intentionally empty so the dashboard redirects to the
    /// onboarding wizard. Idempotent: checks for bank-account existence before adding.
    /// </summary>
    private static async Task EnsureOrgContentAsync(AppDbContext db, IServiceProvider sp, CancellationToken ct)
    {
        // Org settings (required for settings page / PM branding).
        if (!await db.Set<OrgSettings>().AnyAsync(ct))
        {
            db.Set<OrgSettings>().Add(new OrgSettings
            {
                Id = UuidV7.NewId(),
                AccountingBasis = AccountingBasis.Cash,
                MoneyNegativeDisplay = MoneyNegativeDisplay.Minus,
                LegalName = "Cutover Demo PM",
                City = "Raleigh",
                State = "NC",
            });
            await db.SaveChangesAsync(ct);
        }

        // Bank accounts (Trust + Deposit) — names must match bank_balances.csv column "Account Name".
        // Idempotent: the stable ids + (org_id, id) PK means a re-seed finds the existing rows.
        if (!await db.Set<BankAccount>().AnyAsync(b => b.Id == OperatingTrustId, ct))
        {
            db.Set<BankAccount>().Add(new BankAccount
            {
                Id = OperatingTrustId,
                Name = "Cutover Operating Trust",
                Institution = "First National",
                Mask = "1001",
                Purpose = DirectoryBankPurpose.Trust,
            });
            await db.SaveChangesAsync(ct);
        }

        if (!await db.Set<BankAccount>().AnyAsync(b => b.Id == DepositTrustId, ct))
        {
            db.Set<BankAccount>().Add(new BankAccount
            {
                Id = DepositTrustId,
                Name = "Cutover Deposit Trust",
                Institution = "First National",
                Mask = "1002",
                Purpose = DirectoryBankPurpose.Deposit,
            });
            await db.SaveChangesAsync(ct);
        }

        // Chart of accounts — provisions the fixed accounts + per-bank accounts.
        // Idempotent by (org_id, code) unique index.
        var chartOfAccounts = sp.GetRequiredService<IChartOfAccounts>();
        await chartOfAccounts.ProvisionAsync(
            [
                new BankAccountSpec(OperatingTrustId, "Cutover Operating Trust", AccountingBankPurpose.Trust),
                new BankAccountSpec(DepositTrustId, "Cutover Deposit Trust", AccountingBankPurpose.Deposit),
            ], ct);

        // Audit the first provision. Skip on re-seed.
        if (!await db.AuditEvents.AnyAsync(e => e.EntityType == ProvisionAuditEntityType, ct))
        {
            db.AuditEvents.Add(new AuditEvent
            {
                Id = UuidV7.NewId(),
                EntityType = ProvisionAuditEntityType,
                EntityId = CutoverOrgId,
                Action = "seed",
                After = System.Text.Json.JsonSerializer.Serialize(new { org = "cutover" }),
                OccurredAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
    }
}
