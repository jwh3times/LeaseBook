using System.Text;
using System.Text.Json;
using LeaseBook.Migrator;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.Modules.Banking.Features.Import;
using LeaseBook.Modules.Banking.Import;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Operations.Runs;
using LeaseBook.Modules.Reporting.Delivery;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Onboarding;
using LeaseBook.Web.Onboarding.Verification;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Reporting;
using LeaseBook.Web.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using AccountingBankPurpose = LeaseBook.Modules.Accounting.Contracts.BankPurpose;
using DirectoryBankPurpose = LeaseBook.Modules.Directory.Domain.BankPurpose;
using DirLateFeeKind = LeaseBook.Modules.Directory.Domain.LateFeeKind;

namespace LeaseBook.Web.Seeding;

/// <summary>
/// Provisions the synthetic <b>scenario</b> org — the all-scenario fixture (M8 / WP-13) that
/// exercises every live-org functionality at hand-verifiable density: every posting template,
/// every workflow, every report, with multiple PMAdmin and multiple PMStaff users.
///
/// <para>
/// Shape: 5 owners / 6 properties / 11 units / 8 tenants, cutover <b>2026-02-28</b>, four months
/// of activity <b>March–June 2026</b>. Unlike demo (hand-authored showcase) and load (PRNG scale),
/// every figure here is a hand-authored literal and every posting goes through the engine: the
/// real M7 import path provisions the org (including an accrual≠cash opening delta, a
/// pre-sign-off supersede, and a held-PM-fees opening position), the M6 <see cref="RunEngine"/>
/// drives rent/late-fee/disbursement, <see cref="ISender"/> commands drive everything a command
/// exists for, and <see cref="IAccountingEvents"/> covers only the four commandless events
/// (<c>PMFeesSwept</c>, <c>OwnerContribution</c>, <c>VendorPaid</c>, <c>RefundIssued</c>).
/// Maintained fixture contract: <c>docs/runbooks/local-dev.md</c>, "Seeded fixture organizations".
/// </para>
///
/// <para>
/// Load-bearing hand-authored tripwires (any posting-template or rounding change moves them —
/// that is the point; see <c>ScenarioGoldenTests</c>):
/// <list type="bullet">
/// <item>O-S3 parked exactly at the reserve floor: 1,052.63 − fee 52.63 = reserve 1,000.00 →
/// <c>below_reserve_floor</c> every disbursement run, no posting, equity never moves.</item>
/// <item>O-S4 negative cash equity (−250.00, imported) → <c>non_positive_equity</c> every run.</item>
/// <item>Opening tie-out identities: Σ imported bank books = Σ owner cash equity + Σ deposits +
/// Σ held fees, and Σ accrual deltas = Σ opening receivables (450.00) — either failing makes
/// migration sign-off refuse.</item>
/// <item>Sweeps are literals, never computed: April 1,800.00 (partial — leaves 57.80 so the
/// back-dated April 20 bank fee cannot drive held fees negative), May 752.97 (full).</item>
/// <item><see cref="ExpectedLifetimePmIncome"/> pins lifetime <c>pm_income</c> = 2,936.71.</item>
/// </list>
/// </para>
/// </summary>
public static class ScenarioSeeder
{
    public static readonly Guid ScenarioOrgId = new("01923000-0000-7000-8000-0000005ce400");

    public const string AdminEmail = "admin@scenario.test";
    public const string Admin2Email = "admin2@scenario.test";
    public const string StaffEmail = "staff@scenario.test";
    public const string Staff2Email = "staff2@scenario.test";

    /// <summary>DEV ONLY documented seed passwords — the F1 SeederGuard posture (guard, not secrecy).</summary>
    public const string AdminPassword = "Scenario-Trust-2026!";
    public const string StaffPassword = "Scenario-Staff-2026!";

    /// <summary>
    /// Committed Base32 TOTP secret for <see cref="Admin2Email"/> (the enrolled admin). Written
    /// through the identity-token encryption converter, so it lands as ciphertext at rest while
    /// remaining a deterministic source-committed credential — the same F1-sanctioned posture as
    /// the seed passwords. Compute codes with <c>AuthTestSupport.ComputeTotp</c>.
    /// </summary>
    public const string Admin2TotpSecret = "LEASEBOOK2SCENARIO3TOTP4KEY5AA66";

    /// <summary>Stable bank-account ids so chart-of-accounts provisioning is idempotent.</summary>
    public static readonly Guid OperatingTrustId = new("01923000-0000-7000-8000-00005ce4ba01");
    public static readonly Guid DepositTrustId = new("01923000-0000-7000-8000-00005ce4ba02");
    public static readonly Guid PmOperatingId = new("01923000-0000-7000-8000-00005ce4ba03");

    private const string ProvisionAuditEntityType = "org-provisioned";

    /// <summary>Cutover date — opening positions post here through the real M7 import path.</summary>
    public static readonly DateOnly Cutover = new(2026, 2, 28);

    // ── Opening tie-out (design note §3; both identities gate migration sign-off) ──
    private const decimal OpeningOperatingTrust = 15_002.63m;
    private const decimal OpeningDepositTrust = 3_600.00m;
    private const decimal OpeningOwnerEquityTotal = 14_652.63m; // 8,250 + 3,000 + 1,052.63 − 250 + 2,600
    private const decimal OpeningDepositLiabilityTotal = 3_600.00m; // 1,200 + 1,000 + 1,400
    private const decimal OpeningHeldPmFees = 350.00m;
    private const decimal OpeningAccrualDeltaTotal = 450.00m; // O-S4 accrual 200 − cash (−250)
    private const decimal OpeningReceivableTotal = 450.00m; // T-S6

    // ── Hand-authored sweep literals + their post-sweep held positions (§5 O4/R11) ──
    private const decimal AprilSweepAmount = 1_800.00m;
    private const decimal HeldAfterAprilSweep = 57.80m; // 350 + 1,146.60 (Mar fees) + 361.20 (Apr fees) − 1,800
    private const decimal MaySweepAmount = 752.97m; // 42.80 residue + 710.17 (May fees) — full sweep
    private const decimal HeldAfterMarchRun = 1_496.60m; // the standing position across 2026-03-31

    /// <summary>
    /// Golden pin: lifetime <c>pm_income</c> (cash+both, all banks) after the four-month window.
    /// = opening held 350.00 + fees (1,146.60 + 361.20 + 710.17 + 379.90 = 2,597.87) − bank fees
    /// (15.00 back-dated April + 15.00 June + 8.50 created-from-import June = 38.50) + interest
    /// 12.34. Sweeps and trust transfers net to zero org-wide, so this also equals end-state held
    /// fees 368.74 + the swept PM-operating position 2,552.97. <b>Intended-until-C1</b> (ADR-018
    /// equity-basis fees): a deliberate fee-basis change moves this constant in the same commit.
    /// </summary>
    private const decimal ExpectedLifetimePmIncome = 2_921.71m;

    /// <summary>End-state held PM fees per trust bank at the golden as-of (2026-06-30).</summary>
    private const decimal ExpectedHeldOperatingEnd = 368.74m; // 379.90 − 15.00 − 8.50 + 12.34
    private const decimal ExpectedHeldDepositEnd = 0.00m; // 12.34 interest − 12.34 transfer

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        SeederGuard.RequireNonProduction(services);

        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        await RoleSeeder.EnsureRolesAsync(sp, ct);
        await EnsureOrgAsync(sp.GetRequiredService<AppDbContext>(), ct);
        var adminId = await EnsureUsersAsync(sp.GetRequiredService<UserManager<AppUser>>(), ct);

        var executor = sp.GetRequiredService<OrgScopedExecutor>();
        var db = sp.GetRequiredService<AppDbContext>();
        // Imports/sign-off/audit rows should carry a real actor, not "system" (design §2 R4). This
        // used to be a hand-written ActorContext assignment inside the work, because the executor
        // had no way to carry it.
        await executor.RunAsync(ScenarioOrgId, Actor.User(adminId), async () =>
        {
            if (await db.Set<JournalEntry>().AnyAsync(ct))
            {
                // Already seeded (idempotent — the journal is the anchor); the pins still hold.
                await VerifyPinsAsync(sp, db, ct);
                return;
            }

            if (!await db.AuditEvents.AnyAsync(e => e.EntityType == ProvisionAuditEntityType, ct))
            {
                db.AuditEvents.Add(new AuditEvent
                {
                    Id = UuidV7.NewId(),
                    EntityType = ProvisionAuditEntityType,
                    EntityId = ScenarioOrgId,
                    Action = "seed",
                    After = JsonSerializer.Serialize(new { org = "scenario", cutover = Cutover }),
                    OccurredAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }

            await EnsureOrgContentAsync(db, sp, ct);
            await ImportEntitiesAsync(sp, ct);
            var ids = await LoadDirectoryIdsAsync(db, sp, ct);
            await ImportOpeningBalancesAndSignOffAsync(sp, db, ct);

            var ctx = new Ctx(
                sp.GetRequiredService<ISender>(),
                sp.GetRequiredService<IAccountingEvents>(),
                sp.GetRequiredService<RunEngine>(),
                db, ids,
                sp.GetRequiredService<ITenantPostingDimensions>(),
                assessmentDate => CreateHistoricalRunEngine(sp, db, assessmentDate));

            await PostMarchAsync(ctx, ct);
            await PostAprilAsync(ctx, ct);
            await PostMayAsync(sp, ctx, ct);
            await PostJuneAsync(ctx, ct);
            await SeedDeliveryHistoriesAsync(sp, db, ids, ct);

            await VerifyPinsAsync(sp, db, ct);
        }, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Org, users, settings, banks
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task EnsureOrgAsync(AppDbContext db, CancellationToken ct)
    {
        if (!await db.Orgs.AnyAsync(o => o.Id == ScenarioOrgId, ct))
        {
            db.Orgs.Add(new Org { Id = ScenarioOrgId, Name = "Scenario Full-Coverage PM" });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Two PMAdmin (one TOTP-enrolled via a deterministic committed key, one un-enrolled) and two
    /// PMStaff — the live role matrix. No other seed provisions a PMStaff user, so this closes the
    /// pen-test residual (the PMStaff boundary was never exercisable against a running host).
    /// Returns the primary admin's id for actor attribution.
    /// </summary>
    private static async Task<Guid> EnsureUsersAsync(UserManager<AppUser> userManager, CancellationToken ct)
    {
        var adminId = await EnsureUserAsync(userManager, AdminEmail, "Scenario Admin", AdminPassword, Roles.PMAdmin);
        var admin2Id = await EnsureUserAsync(userManager, Admin2Email, "Scenario Admin (MFA)", AdminPassword, Roles.PMAdmin);
        await EnsureUserAsync(userManager, StaffEmail, "Scenario Staff", StaffPassword, Roles.PMStaff);
        await EnsureUserAsync(userManager, Staff2Email, "Scenario Staff Two", StaffPassword, Roles.PMStaff);

        // TOTP-enrol the second admin with the deterministic committed key. The token store writes
        // through the at-rest encryption converter; ResetAuthenticatorKeyAsync would generate a
        // random key, which is exactly what a deterministic fixture must not do.
        var admin2 = await userManager.FindByIdAsync(admin2Id.ToString())
            ?? throw new InvalidOperationException("Scenario admin2 vanished between create and enrol.");
        if (!await userManager.GetTwoFactorEnabledAsync(admin2))
        {
            await userManager.SetAuthenticationTokenAsync(
                admin2, "[AspNetUserStore]", "AuthenticatorKey", Admin2TotpSecret);
            await userManager.SetTwoFactorEnabledAsync(admin2, true);
        }

        return adminId;
    }

    private static async Task<Guid> EnsureUserAsync(
        UserManager<AppUser> userManager, string email, string displayName, string password, string role)
    {
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            return existing.Id;
        }

        var user = new AppUser
        {
            Id = UuidV7.NewId(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            OrgId = ScenarioOrgId,
            DisplayName = displayName,
        };

        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to seed scenario user {email}: " +
                string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        await userManager.AddToRoleAsync(user, role);
        return user.Id;
    }

    private static async Task EnsureOrgContentAsync(AppDbContext db, IServiceProvider sp, CancellationToken ct)
    {
        if (!await db.Set<OrgSettings>().AnyAsync(ct))
        {
            db.Set<OrgSettings>().Add(new OrgSettings
            {
                Id = UuidV7.NewId(),
                AccountingBasis = AccountingBasis.Accrual,
                MoneyNegativeDisplay = MoneyNegativeDisplay.Minus,
                LegalName = "Scenario Full-Coverage PM",
                City = "Raleigh",
                State = "NC",
                // Org-default late-fee policy: percent 400 bps, so the percent-under-cap clamp path
                // fires for any lease without an override (T-S8). T-S2/T-S6 carry flat overrides.
                RentDueDay = 1,
                LateFeeGraceDays = 5,
                LateFeeKind = DirLateFeeKind.Percent,
                LateFeeRateBps = 400,
            });
            await db.SaveChangesAsync(ct);
        }

        // Exactly ONE Trust-purpose bank by design: the report-preview resolver orders CreatedAt
        // desc while the balance importer orders asc — a second trust bank would silently split
        // them (design §3 O5). The Deposit-purpose bank is trust_bank *class* but not Trust purpose.
        await EnsureBankAsync(db, OperatingTrustId, "Scenario Operating Trust", "2201", DirectoryBankPurpose.Trust, ct);
        await EnsureBankAsync(db, DepositTrustId, "Scenario Deposit Trust", "2202", DirectoryBankPurpose.Deposit, ct);
        await EnsureBankAsync(db, PmOperatingId, "Scenario PM Operating", "2203", DirectoryBankPurpose.Operating, ct);

        await sp.GetRequiredService<IChartOfAccounts>().ProvisionAsync(
            [
                new BankAccountSpec(OperatingTrustId, "Scenario Operating Trust", AccountingBankPurpose.Trust),
                new BankAccountSpec(DepositTrustId, "Scenario Deposit Trust", AccountingBankPurpose.Deposit),
                new BankAccountSpec(PmOperatingId, "Scenario PM Operating", AccountingBankPurpose.Operating),
            ], ct);
    }

    private static async Task EnsureBankAsync(
        AppDbContext db, Guid id, string name, string mask, DirectoryBankPurpose purpose, CancellationToken ct)
    {
        if (!await db.Set<BankAccount>().AnyAsync(b => b.Id == id, ct))
        {
            db.Set<BankAccount>().Add(new BankAccount
            {
                Id = id,
                Name = name,
                Institution = "Coastal Carolina Bank",
                Mask = mask,
                Purpose = purpose,
            });
            await db.SaveChangesAsync(ct);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // M7 provenance: entity import → balances → supersede → verify → sign-off
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task ImportEntitiesAsync(IServiceProvider sp, CancellationToken ct)
    {
        var entities = sp.GetRequiredService<EntityImportService>();
        await ImportEntityAsync(entities, AppFolioImportCatalog.Owners, "owners.csv", ct);
        await ImportEntityAsync(entities, AppFolioImportCatalog.Properties, "properties.csv", ct);
        await ImportEntityAsync(entities, AppFolioImportCatalog.Units, "units.csv", ct);
        await ImportEntityAsync(entities, AppFolioImportCatalog.TenantsLeases, "tenants_leases.csv", ct);
    }

    private static async Task ImportEntityAsync(
        EntityImportService entities,
        AppFolioImportDefinition definition,
        string fixtureFile,
        CancellationToken ct)
    {
        await using var stream = OpenFixture(fixtureFile);
        var result = await entities.ImportAsync(definition, fixtureFile, stream, ct);
        Assert(result.ErrorCount == 0,
            $"scenario entity import {fixtureFile}: {result.ErrorCount} row error(s) — " +
            string.Join("; ", result.Errors.Select(e => $"row {e.RowNumber} {e.Field}: {e.Reason}")));
    }

    private static async Task ImportOpeningBalancesAndSignOffAsync(
        IServiceProvider sp, AppDbContext db, CancellationToken ct)
    {
        var balances = sp.GetRequiredService<BalanceImportService>();

        await ImportBalanceAsync(balances, AppFolioImportCatalog.BankBalances, "bank_balances.csv", ct);
        await ImportBalanceAsync(balances, AppFolioImportCatalog.OwnerBalances, "owner_balances.csv", ct);
        await ImportBalanceAsync(balances, AppFolioImportCatalog.DepositLiabilities, "deposit_liabilities.csv", ct);
        await ImportBalanceAsync(balances, AppFolioImportCatalog.TenantReceivables, "tenant_receivables.csv", ct);
        await ImportBalanceAsync(balances, AppFolioImportCatalog.HeldPmFees, "held_pm_fees.csv", ct);

        // WP-7 Half-A provenance: the initial owner_balances deliberately understates O-S1
        // (8,000.00); the corrected file supersedes it to 8,250.00 and leaves the other four
        // untouched — exercising both the Superseded and Unchanged outcomes pre-sign-off.
        await using (var corrected = OpenFixture("owner_balances_corrected.csv"))
        {
            var supersede = await balances.SupersedeAsync(
                AppFolioImportCatalog.OwnerBalances, "owner_balances_corrected.csv", Cutover, corrected, ct);
            Assert(supersede.Counts.Superseded == 1 && supersede.Counts.Unchanged == 4,
                $"scenario supersede: expected 1 superseded / 4 unchanged, got " +
                $"{supersede.Counts.Superseded}/{supersede.Counts.Unchanged}");
        }

        // Both opening tie-out identities, asserted against the journal itself (design §3 D6/O3):
        // the migration_clearing account must net to exactly zero per basis, or sign-off refuses.
        var clearingCash = await ClearingResidualAsync(db, "cash", ct);
        var clearingAccrual = await ClearingResidualAsync(db, "accrual", ct);
        Assert(clearingCash == 0m,
            $"scenario opening cash identity broken: migration_clearing nets {clearingCash:0.00} " +
            "(Σ bank books must equal Σ owner cash equity + Σ deposits + Σ held fees)");
        Assert(clearingAccrual == 0m,
            $"scenario opening accrual identity broken: migration_clearing nets {clearingAccrual:0.00} " +
            $"(Σ accrual deltas {OpeningAccrualDeltaTotal:0.00} must equal Σ opening receivables " +
            $"{OpeningReceivableTotal:0.00})");

        var verification = sp.GetRequiredService<VerificationService>();
        var report = await verification.VerifyAsync(
            new VerificationRequest(
                Cutover,
                OpeningOwnerEquityTotal,
                OpeningDepositLiabilityTotal,
                [
                    new OperatorBankBalance(OperatingTrustId, OpeningOperatingTrust),
                    new OperatorBankBalance(DepositTrustId, OpeningDepositTrust),
                ],
                HeldPmFeesTotal: OpeningHeldPmFees), ct);
        Assert(report.IsTied, $"scenario verification not tied: variance {report.VarianceTotal:0.00}");

        await verification.SignOffAsync(report.VerificationId, ct);
        db.ChangeTracker.Clear();
    }

    private static async Task ImportBalanceAsync(
        BalanceImportService balances,
        AppFolioImportDefinition definition,
        string fixtureFile,
        CancellationToken ct)
    {
        await using var stream = OpenFixture(fixtureFile);
        var result = await balances.ImportAsync(definition, fixtureFile, Cutover, stream, ct);
        Assert(result.ErrorCount == 0,
            $"scenario balance import {fixtureFile}: {result.ErrorCount} row error(s) — " +
            string.Join("; ", result.Errors.Select(e => $"row {e.RowNumber} {e.Field}: {e.Reason}")));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Directory shaping after import (ids, fee bps, per-lease policy overrides)
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record DirectoryIds(
        IReadOnlyDictionary<string, Guid> Owners,
        IReadOnlyDictionary<string, Guid> Tenants);

    /// <summary>
    /// The entity import path cannot carry management-fee bps (CreateOwner receives none from the
    /// importer) or per-lease late-fee overrides, so both are shaped here after import: bps via the
    /// real <see cref="Modules.Directory.Features.Owners.UpdateOwner"/> command, lease overrides
    /// directly on <see cref="LeaseLite"/> (directory data, host-side — the same altitude as
    /// DemoDirectorySeed; WP-6 will give overrides a real settings surface).
    /// </summary>
    private static async Task<DirectoryIds> LoadDirectoryIdsAsync(
        AppDbContext db, IServiceProvider sp, CancellationToken ct)
    {
        var sender = sp.GetRequiredService<ISender>();

        var owners = await db.Set<Owner>().Where(o => !o.IsSystem)
            .ToDictionaryAsync(o => o.Name, o => o.Id, ct);
        var tenants = await db.Set<Tenant>().Where(t => !t.IsSystem)
            .ToDictionaryAsync(t => t.DisplayName, t => t.Id, ct);

        // (name, bps, reserve) — reserve re-stated because UpdateOwner is a full update.
        (string Name, int? Bps, decimal Reserve)[] feePlan =
        [
            ("Harborview Holdings", 700, 500.00m),
            ("Beacon Ridge LLC", 0, 0.00m),
            ("Stillwater Properties", 500, 1_000.00m),
            ("Meridian Estates", 600, 0.00m),
            ("Cypress Grove Partners", 800, 250.00m),
        ];
        foreach (var (name, bps, reserve) in feePlan)
        {
            var updated = await sender.Send(
                new Modules.Directory.Features.Owners.UpdateOwner(
                    owners[name], name, null, null, null, bps, reserve), ct);
            Assert(updated, $"scenario UpdateOwner({name}) returned false");
        }

        // Per-lease late-fee overrides: T-S2 flat 50.00 (exactly at the 5% cap for rent 1,000 —
        // the inclusive-boundary case), T-S6 flat 20.00 on rent 200 (floor-clamps to 15.00).
        await SetLeaseOverrideAsync(db, tenants["Rosa Delgado"], 50.00m, ct);
        await SetLeaseOverrideAsync(db, tenants["Dean Ashford"], 20.00m, ct);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        return new DirectoryIds(owners, tenants);
    }

    private static async Task SetLeaseOverrideAsync(
        AppDbContext db, Guid tenantId, decimal flatAmount, CancellationToken ct)
    {
        var lease = await db.Set<LeaseLite>().SingleAsync(l => l.TenantId == tenantId, ct);
        lease.LateFeeKindOverride = DirLateFeeKind.Flat;
        lease.LateFeeAmountOverride = flatAmount;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The four-month timeline (design note §5)
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record Ctx(
        ISender Sender, IAccountingEvents Events, RunEngine Engine, AppDbContext Db, DirectoryIds Ids,
        ITenantPostingDimensions Dimensions, Func<DateOnly, RunEngine> HistoricalRunEngine)
    {
        public Guid Tenant(string name) => Ids.Tenants[name];

        public Guid Owner(string name) => Ids.Owners[name];

        /// <summary>
        /// The owner/property a tenant's postings carry — the same resolution the ledger commands use.
        /// Needed for the commandless <c>RefundIssued</c> deposit path, whose liability debit must release
        /// the owner bucket its collection credited (I7).
        /// </summary>
        public async Task<TenantPostingDimensions> DimsAsync(
            Guid tenantId, DateOnly date, CancellationToken ct) =>
            await Dimensions.GetAsync(tenantId, date, ct)
            ?? throw new InvalidOperationException(
                $"Scenario seed: tenant {tenantId} has no lease effective on {date:yyyy-MM-dd}.");
    }

    /// <summary>
    /// March: full run cycle #1. T-S2 partial payment feeds the at-cap late fee; T-S6 (never pays)
    /// feeds the floor-clamped fee; the disbursement run fires both exclusion reasons (O-S3
    /// below_reserve_floor at exactly the floor, O-S4 non_positive_equity) and assesses
    /// 819.00 (O-S1) + 327.60 (O-S5) = 1,146.60 of fees, which then <b>stand across the March
    /// period end</b> — the standing held-PM-fees position the roadmap requires.
    /// </summary>
    private static async Task PostMarchAsync(Ctx ctx, CancellationToken ct)
    {
        await ConfirmRunAsync(ctx, RunType.Rent, 2026, 3, ct);

        await PayAsync(ctx, "Avery Whitfield", 1_450.00m, new(2026, 3, 3), "ach", ct);
        await PayAsync(ctx, "Rosa Delgado", 600.00m, new(2026, 3, 10), "check", ct); // partial
        await PayAsync(ctx, "Priya Raman", 1_400.00m, new(2026, 3, 5), "check", ct);
        await PayAsync(ctx, "Marcus Vale", 1_500.00m, new(2026, 3, 4), "card", ct);
        await PayAsync(ctx, "Silas Byrd", 1_375.00m, new(2026, 3, 6), "cash", ct);

        // Maintenance-recharge history #1 (T-S8), paid two weeks later.
        await ctx.Sender.Send(new AddCharge(
            ctx.Tenant("Silas Byrd"), 120.00m, new(2026, 3, 12), "maintenance-recharge",
            "Disposal repair recharge", "scenario:recharge:2026-03:t-s8"), ct);
        await PayAsync(ctx, "Silas Byrd", 120.00m, new(2026, 3, 20), "cash", ct, "recharge");

        await ConfirmRunAsync(ctx, RunType.LateFee, 2026, 3, ct, new(2026, 3, 20)); // T-S2 50.00 at-cap, T-S6 15.00 floor
        await ConfirmRunAsync(ctx, RunType.Disbursement, 2026, 3, ct);

        await AssertHeldAsync(ctx.Db, OperatingTrustId, HeldAfterMarchRun,
            "held PM fees standing across the March period end", ct);

        await ReconcileAndFinalizeAsync(ctx, 2026, 3, ct);
        ctx.Db.ChangeTracker.Clear();
    }

    /// <summary>
    /// April: T-S3's mid-month lease start prorates (1,620 × 15/30 = 810.00, the clean ADR-017
    /// figure) and the rent run is immediately re-run to prove idempotency (second run finds
    /// nothing to do). T-S8 misses the month — the percent-under-cap late-fee carrier. O-S5 joins
    /// the excluded set this month (equity 250 parks it under its own reserve). The April sweep is
    /// a hand-authored <b>partial</b> literal, then the finalized month is unlocked with a reason,
    /// a back-dated bank fee lands, and the month re-finalizes — the Reopened → Finalized path.
    /// </summary>
    private static async Task PostAprilAsync(Ctx ctx, CancellationToken ct)
    {
        await ctx.Sender.Send(new CollectDeposit(
            ctx.Tenant("Kenji Nakamura"), 1_620.00m, new(2026, 4, 16), DepositTrustId,
            "Security deposit — move-in", "scenario:deposit:t-s3"), ct);

        var april = await ConfirmRunAsync(ctx, RunType.Rent, 2026, 4, ct);
        Assert(april.Any(r => r.Amount == 810.00m),
            "scenario April rent run: expected the 810.00 ADR-017 proration row for T-S3");

        // Idempotent re-run: everything is already charged, so nothing is selectable.
        var rerun = await ctx.Engine.PreviewAsync(RunType.Rent, new RunPeriod(2026, 4), ct);
        Assert(rerun.Rows.All(r => r.AlreadyDone || r.ExcludedReason is not null),
            "scenario April rent re-run: expected every row AlreadyDone/excluded");

        await PayAsync(ctx, "Avery Whitfield", 1_450.00m, new(2026, 4, 3), "ach", ct);
        await PayAsync(ctx, "Rosa Delgado", 1_000.00m, new(2026, 4, 8), "check", ct);
        await ctx.Sender.Send(new IssueCredit(
            ctx.Tenant("Rosa Delgado"), 85.00m, new(2026, 4, 14),
            "Goodwill credit — maintenance delay", "scenario:credit:t-s2"), ct);
        await PayAsync(ctx, "Kenji Nakamura", 810.00m, new(2026, 4, 20), "ach", ct);
        await PayAsync(ctx, "Priya Raman", 1_400.00m, new(2026, 4, 5), "check", ct);
        await PayAsync(ctx, "Marcus Vale", 1_500.00m, new(2026, 4, 4), "card", ct);
        // T-S8 misses April entirely — feeds the percent-path late fee (400 bps × 1,375 = 55.00).

        await ConfirmRunAsync(ctx, RunType.LateFee, 2026, 4, ct, new(2026, 4, 20));
        await ConfirmRunAsync(ctx, RunType.Disbursement, 2026, 4, ct);

        // Partial sweep literal (R11): held is 1,857.80 here; sweeping it all would let the
        // back-dated April 20 bank fee below drive held fees negative with no code guard.
        await ctx.Events.PostAsync(new PMFeesSwept(
            new Money(AprilSweepAmount), new(2026, 4, 10), OperatingTrustId, PmOperatingId,
            "Mgmt fee sweep → PM Operating — 2026-04 (partial)", "scenario:sweep:2026-04"), ct);
        await AssertHeldAsync(ctx.Db, OperatingTrustId, HeldAfterAprilSweep,
            "held PM fees after the April partial sweep", ct);

        await ReconcileAndFinalizeAsync(ctx, 2026, 4, ct);

        // Unlock-with-reason (PMAdmin-only surface), land the correction, re-finalize.
        var recon = await ctx.Db.Set<BankReconciliation>().AsNoTracking().SingleAsync(
            r => r.BankAccountId == OperatingTrustId && r.PeriodYear == 2026 && r.PeriodMonth == 4, ct);
        await ctx.Sender.Send(new UnlockReconciliation(
            recon.Id, "Bank statement showed an analysis fee the register was missing"), ct);
        await ctx.Sender.Send(new RecordBankAdjustment(
            "fee", 15.00m, new(2026, 4, 20), OperatingTrustId, null,
            "Monthly account analysis fee", "scenario:bankfee:2026-04"), ct);
        await ReconcileAndFinalizeAsync(ctx, 2026, 4, ct, banks: [OperatingTrustId]);
        ctx.Db.ChangeTracker.Clear();
    }

    /// <summary>
    /// May: T-S4's move-out — proration 1,400 × 15/31 = 677.42, then the full deposit disposition
    /// (apply against charges + apply to owner income + refund the 422.58 remainder <b>on the
    /// Deposit Trust</b> — the refund template does not validate its bank target, so the bank is
    /// load-bearing by hand). T-S5's 1,700.00 payment splits exactly 200.00 into prepayments.
    /// Interest lands on the Deposit Trust; O-S1 and O-S5 contribute (O-S1's gives its delivered
    /// May statement the Contributions section); the vendor payment draws on O-S5. The May sweep
    /// literal takes held fees to exactly zero, and the O-S1 consolidated statement is delivered
    /// through the real seam (tie-out gate → PDF → artifact → Queued record).
    /// </summary>
    private static async Task PostMayAsync(IServiceProvider sp, Ctx ctx, CancellationToken ct)
    {
        await ConfirmRunAsync(ctx, RunType.Rent, 2026, 5, ct);

        await PayAsync(ctx, "Avery Whitfield", 1_450.00m, new(2026, 5, 3), "ach", ct);
        await PayAsync(ctx, "Rosa Delgado", 1_415.00m, new(2026, 5, 12), "check", ct); // full catch-up
        await PayAsync(ctx, "Kenji Nakamura", 1_620.00m, new(2026, 5, 3), "ach", ct);
        await PayAsync(ctx, "Marcus Vale", 1_700.00m, new(2026, 5, 4), "card", ct); // → 200.00 prepayment
        await PayAsync(ctx, "Silas Byrd", 2_805.00m, new(2026, 5, 10), "cash", ct); // Apr+fee+May catch-up

        // Maintenance-recharge history #2.
        await ctx.Sender.Send(new AddCharge(
            ctx.Tenant("Silas Byrd"), 95.00m, new(2026, 5, 12), "maintenance-recharge",
            "Screen door recharge", "scenario:recharge:2026-05:t-s8"), ct);
        await PayAsync(ctx, "Silas Byrd", 95.00m, new(2026, 5, 21), "cash", ct, "recharge");

        // T-S4 move-out disposition is dated on the inclusive final lease day. The receivable is
        // exactly the 677.42 proration (Mar/Apr paid in full), so against-charges consumes it to the
        // cent; damages are unguarded by design; the refund empties the held deposit to exactly zero
        // (I4 floor). A later date would have no effective lease from which to derive attribution.
        var tS4 = ctx.Tenant("Priya Raman");
        await ctx.Sender.Send(new ApplyDeposit(
            tS4, 677.42m, new(2026, 5, 15), DepositTrustId, OperatingTrustId,
            "against-charges", "Move-out: final prorated rent", "scenario:depapply:charges:t-s4"), ct);
        await ctx.Sender.Send(new ApplyDeposit(
            tS4, 300.00m, new(2026, 5, 15), DepositTrustId, OperatingTrustId,
            "to-owner-income", "Move-out: carpet damage", "scenario:depapply:damages:t-s4"), ct);
        var tS4Dims = await ctx.DimsAsync(tS4, new(2026, 5, 15), ct);
        await ctx.Events.PostAsync(new RefundIssued(
            tS4, new Money(422.58m), new(2026, 5, 15), DepositTrustId, RefundSource.Deposits,
            "Deposit refund check #2101 — Raman move-out", "scenario:refund:deposit:t-s4",
            tS4Dims.PropertyId, tS4Dims.OwnerId), ct);

        // Explicit prepayment collection (distinct from the overpay auto-split path).
        await ctx.Sender.Send(new CollectPrepayment(
            ctx.Tenant("Avery Whitfield"), 500.00m, new(2026, 5, 20), OperatingTrustId,
            "June rent prepayment", "scenario:prepay:t-s1"), ct);

        await ctx.Sender.Send(new RecordBankAdjustment(
            "interest", 12.34m, new(2026, 5, 29), DepositTrustId, null,
            "Deposit trust interest", "scenario:interest:2026-05"), ct);

        var oS1 = ctx.Owner("Harborview Holdings");
        var oS5 = ctx.Owner("Cypress Grove Partners");
        var pS1 = await PropertyIdAsync(ctx.Db, "14 Harborview Ln", ct);
        var pS6 = await PropertyIdAsync(ctx.Db, "940 Cypress Grove Dr", ct);
        await ctx.Events.PostAsync(new OwnerContribution(
            oS1, pS1, new Money(400.00m), new(2026, 5, 8), OperatingTrustId,
            "Owner contribution — repair float", "scenario:contrib:o-s1"), ct);
        await ctx.Events.PostAsync(new OwnerContribution(
            oS5, pS6, new Money(500.00m), new(2026, 5, 9), OperatingTrustId,
            "Owner contribution — plumbing job float", "scenario:contrib:o-s5"), ct);
        await ctx.Events.PostAsync(new VendorPaid(
            oS5, pS6, new Money(340.00m), new(2026, 5, 15), OperatingTrustId,
            "Asheville Plumbing Co.", "Water heater element replacement",
            "scenario:vendor:o-s5", new Money(250.00m)), ct);

        await ConfirmRunAsync(ctx, RunType.LateFee, 2026, 5, ct, new(2026, 5, 29)); // only T-S6
        await ConfirmRunAsync(ctx, RunType.Disbursement, 2026, 5, ct);

        // Full sweep literal — held lands on exactly zero (asserted; over-sweep would throw).
        await ctx.Events.PostAsync(new PMFeesSwept(
            new Money(MaySweepAmount), new(2026, 5, 10), OperatingTrustId, PmOperatingId,
            "Mgmt fee sweep → PM Operating — 2026-05", "scenario:sweep:2026-05"), ct);
        await AssertHeldAsync(ctx.Db, OperatingTrustId, 0.00m, "held PM fees after the May full sweep", ct);

        await ReconcileAndFinalizeAsync(ctx, 2026, 5, ct);

        // Deliver O-S1's May consolidated statement through the real seam: tie-out gate → PDF →
        // immutable artifact → first attempt → Queued event. This one stays pending; the April
        // histories seeded later carry the provider outcomes (design §7 D1, ADR-040).
        var assembler = sp.GetRequiredService<StatementAssembler>();
        var views = await assembler.BuildAsync([oS1], null, 2026, 5, "accrual", ct);
        var delivery = await sp.GetRequiredService<IStatementDelivery>()
            .DeliverAsync(views[0], "harborview@scenario.test", ct);
        Assert(delivery.Status == DeliveryEventKind.Queued, "scenario delivery: expected Queued");
        ctx.Db.ChangeTracker.Clear();
    }

    /// <summary>
    /// June — the month left open. Prepayment applications and the prepayment refund; T-S7's new
    /// lease collects a deposit that stays awaiting application; the late-fee run precedes the
    /// manual duplicate late fee so the period-charge guard cannot invert the narrative, and the
    /// duplicate is voided (the visible pair in the ledger UI). After the disbursement run, the
    /// bank fee, the trust→trust transfer (12.34 back from the Deposit Trust — exactly the May
    /// interest attribution, leaving no PM money parked there), and the statement import with all
    /// three match tiers + dedup re-import. No reconciliation: the uncleared strip stays populated.
    /// Every hand-placed event is dated ≤ 2026-06-29 so the delinquency Current bucket stays 0.00.
    /// </summary>
    private static async Task PostJuneAsync(Ctx ctx, CancellationToken ct)
    {
        var tS7 = ctx.Tenant("Lena Osei");
        await ctx.Sender.Send(new CollectDeposit(
            tS7, 1_375.00m, new(2026, 6, 1), DepositTrustId,
            "Security deposit — move-in", "scenario:deposit:t-s7"), ct);

        await ConfirmRunAsync(ctx, RunType.Rent, 2026, 6, ct);

        // T-S1: apply 300.00 of the held 500.00, pay the 1,150.00 remainder — the 200.00 residual
        // prepayment must survive at the golden as-of (deposit-liab keeps its prepayment kind).
        await ctx.Sender.Send(new ApplyPrepayment(
            ctx.Tenant("Avery Whitfield"), 300.00m, new(2026, 6, 5), OperatingTrustId,
            "Prepayment applied — June rent", "scenario:prepapply:t-s1"), ct);
        await PayAsync(ctx, "Avery Whitfield", 1_150.00m, new(2026, 6, 6), "ach", ct);

        // T-S5: apply 150.00 then refund the last 50.00 — the prepayment lifecycle closes at zero.
        await ctx.Sender.Send(new ApplyPrepayment(
            ctx.Tenant("Marcus Vale"), 150.00m, new(2026, 6, 5), OperatingTrustId,
            "Prepayment applied — June rent", "scenario:prepapply:t-s5"), ct);
        await ctx.Events.PostAsync(new RefundIssued(
            ctx.Tenant("Marcus Vale"), new Money(50.00m), new(2026, 6, 6), OperatingTrustId,
            RefundSource.Prepayments, "Prepayment refund check #2102 — Vale", "scenario:refund:prepay:t-s5"), ct);
        await PayAsync(ctx, "Marcus Vale", 1_350.00m, new(2026, 6, 7), "card", ct);

        await PayAsync(ctx, "Kenji Nakamura", 1_620.00m, new(2026, 6, 3), "ach", ct);
        await PayAsync(ctx, "Lena Osei", 1_375.00m, new(2026, 6, 9), "cash", ct);
        await PayAsync(ctx, "Silas Byrd", 1_375.00m, new(2026, 6, 6), "cash", ct);
        // T-S2 pays nothing in June — June's at-cap run fee + the open receivable at as-of.

        await ConfirmRunAsync(ctx, RunType.LateFee, 2026, 6, ct, new(2026, 6, 9));

        // The visible void pair: a duplicate manual late fee posted after the run, then its reversal.
        var duplicate = await ctx.Sender.Send(new AddCharge(
            ctx.Tenant("Rosa Delgado"), 50.00m, new(2026, 6, 18), "late",
            "Late fee — June", "scenario:duplatefee:t-s2"), ct);
        await ctx.Sender.Send(new VoidEntry(
            duplicate.EntryId, "Posted in error — duplicate of the June late-fee run",
            new DateOnly(2026, 6, 19), "scenario:void:duplatefee:t-s2"), ct);

        await ctx.Sender.Send(new AddCharge(
            ctx.Tenant("Rosa Delgado"), 25.00m, new(2026, 6, 20), "other",
            "Returned-check (NSF) fee", "scenario:nsf:t-s2"), ct);

        await ConfirmRunAsync(ctx, RunType.Disbursement, 2026, 6, ct);

        await ctx.Sender.Send(new RecordBankAdjustment(
            "fee", 15.00m, new(2026, 6, 20), OperatingTrustId, null,
            "Monthly account analysis fee", "scenario:bankfee:2026-06"), ct);

        // Trust→trust transfer: sweep May's interest attribution (12.34) back from the Deposit
        // Trust so no PM money stays parked there — cash and its pm_income attribution move
        // together (4 lines), keeping both banks' trust equations independently balanced.
        await ctx.Sender.Send(new RecordBankAdjustment(
            "transfer", 12.34m, new(2026, 6, 23), DepositTrustId, OperatingTrustId,
            "Interest attribution → operating trust", "scenario:transfer:2026-06"), ct);

        await ImportJuneStatementAsync(ctx, ct);
        ctx.Db.ChangeTracker.Clear();
    }

    /// <summary>
    /// The June bank-statement import: a saved column mapping, then one line per match tier —
    /// exact amount ±1 day (matched), exact amount 9 days off (suggested, outside the ±4-day
    /// window but inside the ±45-day read margin), and a bank charge with no register counterpart
    /// (unmatched → recorded as <c>created</c> against a bank adjustment posted on the spot).
    /// A byte-identical re-import then dedups to zero new lines.
    /// </summary>
    private static async Task ImportJuneStatementAsync(Ctx ctx, CancellationToken ct)
    {
        const string csv =
            """
            Date,Description,Amount
            2026-06-07,ACH DEPOSIT WHITFIELD,1150.00
            2026-06-12,DEPOSIT NAKAMURA #2A,1620.00
            2026-06-24,ACCOUNT ANALYSIS CHARGE,-8.50
            """;
        var map = new ColumnMap("Date", "Description", Amount: "Amount");

        await ctx.Sender.Send(new SaveColumnMapping(OperatingTrustId, "Coastal Carolina CSV", map), ct);

        var import = await ctx.Sender.Send(new ImportStatement(
            OperatingTrustId, "coastal-2026-06.csv", csv, map), ct);
        Assert(import.Imported == 3 && import.SkippedDuplicates == 0,
            $"scenario statement import: expected 3 imported, got {import.Imported} " +
            $"(+{import.SkippedDuplicates} skipped)");

        var preview = await ctx.Sender.Query(new GetMatchPreview(import.ImportId), ct)
            ?? throw new InvalidOperationException("scenario statement import vanished before preview");
        Assert(preview.Summary is { Matched: 1, Suggested: 1, Unmatched: 1 },
            $"scenario match preview: expected 1/1/1, got {preview.Summary.Matched}/" +
            $"{preview.Summary.Suggested}/{preview.Summary.Unmatched}");

        // The unmatched line is a real bank charge the register is missing: create it through the
        // bank-adjustment path, then confirm the decision as `created` against its journal line.
        var created = await ctx.Sender.Send(new RecordBankAdjustment(
            "fee", 8.50m, new(2026, 6, 24), OperatingTrustId, null,
            "Account analysis charge (from statement import)", "scenario:bankfee:analysis:2026-06"), ct);
        var createdLineId = await BankLineIdAsync(ctx.Db, created.EntryId, ct);

        var decisions = preview.Rows.Select(row => row.Kind switch
        {
            "matched" or "suggested" => new MatchDecision(row.StatementLineId, row.JournalLineId, row.Kind),
            _ => new MatchDecision(row.StatementLineId, createdLineId, "created"),
        }).ToList();
        var confirmed = await ctx.Sender.Send(new ConfirmMatches(import.ImportId, decisions), ct);
        Assert(confirmed.Cleared == 3, $"scenario confirm matches: expected 3 cleared, got {confirmed.Cleared}");

        // Dedup: a full re-import of the same file is a no-op.
        var reimport = await ctx.Sender.Send(new ImportStatement(
            OperatingTrustId, "coastal-2026-06-reimport.csv", csv, map), ct);
        Assert(reimport is { Imported: 0, SkippedDuplicates: 3 },
            $"scenario statement re-import: expected 0 imported / 3 skipped, got " +
            $"{reimport.Imported}/{reimport.SkippedDuplicates}");
    }

    /// <summary>
    /// The provider outcomes the local seam cannot produce on its own (design §7 D1, ADR-040): the
    /// April statements for O-S2 and O-S5, issued through the real seam and then walked through the
    /// delivery histories a demo needs to show.
    /// <list type="bullet">
    /// <item>
    /// <b>O-S2</b> — the acceptance-then-bounce case, and the retry that fixes it. The provider
    /// accepted attempt 1 and the recipient's server then bounced it; a second attempt against the
    /// <b>same artifact</b> was accepted and delivered. Both attempts survive, so the run history
    /// shows that the owner did eventually receive exactly the document the first attempt carried.
    /// </item>
    /// <item>
    /// <b>O-S5</b> — the send that never reached the provider at all: one attempt, one Failed event.
    /// </item>
    /// </list>
    /// Together with O-S1's still-Queued May attempt, the scenario org carries every
    /// <see cref="DeliveryEventKind"/> in real rows. Nothing here is hand-inserted — the pre-ADR-040
    /// seeder had to fabricate terminal rows because the seam had no way to express them.
    /// </summary>
    private static async Task SeedDeliveryHistoriesAsync(
        IServiceProvider sp, AppDbContext db, DirectoryIds ids, CancellationToken ct)
    {
        var assembler = sp.GetRequiredService<StatementAssembler>();
        var delivery = sp.GetRequiredService<IStatementDelivery>();

        // ── O-S2: accepted → bounced, then a retry that is accepted → delivered ──────────
        var beaconView = (await assembler.BuildAsync(
            [ids.Owners["Beacon Ridge LLC"]], null, 2026, 4, "accrual", ct))[0];

        var bounced = await delivery.DeliverAsync(beaconView, "beaconridge@scenario.test", ct);
        await delivery.RecordEventAsync(
            bounced.AttemptId, DeliveryEventKind.Accepted,
            new DateTime(2026, 5, 1, 14, 2, 0, DateTimeKind.Utc),
            "scenario-msg-beaconridge-1", null, ct);
        await delivery.RecordEventAsync(
            bounced.AttemptId, DeliveryEventKind.Bounced,
            new DateTime(2026, 5, 1, 14, 9, 0, DateTimeKind.Utc),
            "scenario-msg-beaconridge-1", "550 5.1.1 mailbox unavailable", ct);

        // The retry names the artifact, not the attempt: the owner gets the identical April PDF.
        var retry = await delivery.RetryAsync(bounced.ArtifactId, "accounts@beaconridge.scenario.test", ct);
        await delivery.RecordEventAsync(
            retry.AttemptId, DeliveryEventKind.Accepted,
            new DateTime(2026, 5, 2, 9, 15, 0, DateTimeKind.Utc),
            "scenario-msg-beaconridge-2", null, ct);
        await delivery.RecordEventAsync(
            retry.AttemptId, DeliveryEventKind.Delivered,
            new DateTime(2026, 5, 2, 9, 16, 0, DateTimeKind.Utc),
            "scenario-msg-beaconridge-2", null, ct);

        // ── O-S5: never reached the provider ─────────────────────────────────────────────
        var cypressView = (await assembler.BuildAsync(
            [ids.Owners["Cypress Grove Partners"]], null, 2026, 4, "accrual", ct))[0];

        var failed = await delivery.DeliverAsync(cypressView, "cypressgrove@scenario.test", ct);
        await delivery.RecordEventAsync(
            failed.AttemptId, DeliveryEventKind.Failed,
            new DateTime(2026, 5, 1, 14, 2, 0, DateTimeKind.Utc),
            null, "provider unreachable", ct);

        db.ChangeTracker.Clear();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static Task<PostResult> PayAsync(
        Ctx ctx, string tenantName, decimal amount, DateOnly date, string method,
        CancellationToken ct, string tag = "rent") =>
        ctx.Sender.Send(new RecordPayment(
            ctx.Tenant(tenantName), amount, date, method, OperatingTrustId,
            $"Payment — {tenantName}", $"scenario:payment:{tag}:{date:yyyy-MM-dd}:{tenantName}"), ct);

    /// <summary>
    /// Previews and confirms a run, selecting <b>every</b> row — excluded targets included, so the
    /// run history records the exclusion items (design §5 D7: the run-detail view has both
    /// disbursement exclusion reasons to show). Already-done rows are left unselected.
    /// </summary>
    private static async Task<IReadOnlyList<PreviewRow>> ConfirmRunAsync(
        Ctx ctx, RunType runType, int year, int month, CancellationToken ct,
        DateOnly? historicalAssessmentDate = null)
    {
        // The scenario org replays a historical timeline. Its late-fee runs therefore use the
        // timeline's assessment date, while normal product runs use TimeProvider.System.
        var engine = historicalAssessmentDate is { } assessmentDate
            ? ctx.HistoricalRunEngine(assessmentDate)
            : ctx.Engine;
        var period = new RunPeriod(year, month);
        var preview = await engine.PreviewAsync(runType, period, ct);
        var selected = preview.Rows.Where(r => !r.AlreadyDone).ToList();
        if (selected.Count > 0)
        {
            // Echo the preview's token: the seeder confirms exactly what it previewed, so it takes
            // the same guard a real operator does rather than opting out with null.
            // No acknowledgement: the seeder flips no capability between its runs, so the cross-run
            // guard has nothing to reject. If it ever did, silently overriding it would be the bug.
            await engine.ConfirmAsync(
                runType, period, selected.Select(r => r.TargetId).ToList(), preview.CapabilitiesVersion,
                acknowledgeCapabilityChange: false, ct);
        }

        ctx.Db.ChangeTracker.Clear();
        return selected;
    }

    private static RunEngine CreateHistoricalRunEngine(
        IServiceProvider services, AppDbContext db, DateOnly assessmentDate)
    {
        var clock = new FixedDateTimeProvider(assessmentDate);
        var strategies = services.GetRequiredService<IEnumerable<IRunStrategy>>()
            .Where(strategy => strategy.RunType != RunType.LateFee)
            .Append<IRunStrategy>(new LateFeeRunStrategy(
                services.GetRequiredService<IDelinquencyData>(),
                services.GetRequiredService<ILateFeePolicyData>(),
                services.GetRequiredService<IPostedSourceRefs>(),
                clock));

        return new RunEngine(
            db,
            strategies,
            services.GetRequiredService<IBatchPosting>(),
            clock,
            services.GetRequiredService<IRunPeriodLock>(),
            services.GetRequiredService<ICapabilitySnapshot>());
    }

    private sealed class FixedDateTimeProvider(DateOnly date) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    /// <summary>
    /// Clears every bank line through month end via the real <see cref="ApplyClearances"/> command
    /// (validated, idempotent — unlike the load org's raw upsert), then drives the reconciliation
    /// commands to a $0.00 difference and finalizes. Re-runnable for the April Reopened →
    /// re-finalize leg: <c>StartReconciliation</c> on a non-finalized row re-enters the balance.
    /// </summary>
    private static async Task ReconcileAndFinalizeAsync(
        Ctx ctx, int year, int month, CancellationToken ct, Guid[]? banks = null)
    {
        var monthEnd = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        foreach (var bankId in banks ?? [OperatingTrustId, DepositTrustId, PmOperatingId])
        {
            var lineIds = await ctx.Db.Database.SqlQuery<Guid>(
                $"""
                SELECT jl.id AS "Value"
                FROM journal_lines jl
                JOIN journal_entries e ON e.id = jl.entry_id
                LEFT JOIN bank_line_status s ON s.journal_line_id = jl.id
                WHERE jl.bank_account_id = {bankId}
                  AND jl.account_class IN ('trust_bank', 'pm_operating_bank')
                  AND jl.basis IN ('cash', 'both')
                  AND e.entry_date <= {monthEnd}
                  AND COALESCE(s.status, 'uncleared') = 'uncleared'
                """).ToListAsync(ct);
            if (lineIds.Count > 0)
            {
                await ctx.Sender.Send(new ApplyClearances(lineIds), ct);
            }

            var opened = await ctx.Sender.Send(new StartReconciliation(bankId, year, month, 0m), ct);
            var restated = await ctx.Sender.Send(
                new StartReconciliation(bankId, year, month, opened.ClearedBalance), ct);
            await ctx.Sender.Send(new FinalizeReconciliation(restated.Id), ct);
        }

        ctx.Db.ChangeTracker.Clear();
    }

    private static async Task<Guid> PropertyIdAsync(AppDbContext db, string address, CancellationToken ct) =>
        await db.Set<Property>().Where(p => p.Address == address).Select(p => p.Id).SingleAsync(ct);

    private static async Task<Guid> BankLineIdAsync(AppDbContext db, Guid entryId, CancellationToken ct) =>
        (await db.Database.SqlQuery<Guid>(
            $"""
            SELECT id AS "Value"
            FROM journal_lines
            WHERE entry_id = {entryId} AND account_class IN ('trust_bank', 'pm_operating_bank')
            """).ToListAsync(ct)).Single();

    private static async Task<decimal> HeldPmFeesAsync(AppDbContext db, Guid bankId, CancellationToken ct) =>
        (await db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(COALESCE(credit, 0) - COALESCE(debit, 0)), 0) AS "Value"
            FROM journal_lines
            WHERE account_class = 'pm_income' AND bank_account_id = {bankId}
              AND basis IN ('cash', 'both')
            """).ToListAsync(ct)).Single();

    private static async Task<decimal> ClearingResidualAsync(AppDbContext db, string basis, CancellationToken ct) =>
        (await db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(COALESCE(debit, 0) - COALESCE(credit, 0)), 0) AS "Value"
            FROM journal_lines
            WHERE account_class = 'migration_clearing' AND basis IN ({basis}, 'both')
            """).ToListAsync(ct)).Single();

    private static async Task<decimal> LifetimePmIncomeAsync(AppDbContext db, CancellationToken ct) =>
        (await db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(COALESCE(credit, 0) - COALESCE(debit, 0)), 0) AS "Value"
            FROM journal_lines
            WHERE account_class = 'pm_income' AND basis IN ('cash', 'both')
            """).ToListAsync(ct)).Single();

    private static async Task AssertHeldAsync(
        AppDbContext db, Guid bankId, decimal expected, string what, CancellationToken ct)
    {
        var held = await HeldPmFeesAsync(db, bankId, ct);
        Assert(held == expected, $"scenario {what}: expected {expected:0.00}, observed {held:0.00}");
    }

    /// <summary>
    /// The end-state pins, verified on first seed and every re-run: lifetime pm_income, per-bank
    /// held fees (never negative — bank fees/transfers have no code guard, design §12 finding 9),
    /// and the core invariant sweep (I1–I5) clean.
    /// </summary>
    private static async Task VerifyPinsAsync(IServiceProvider sp, AppDbContext db, CancellationToken ct)
    {
        var lifetime = await LifetimePmIncomeAsync(db, ct);
        Assert(lifetime == ExpectedLifetimePmIncome,
            $"Scenario-org PM income pin violated: observed {lifetime:0.00}, pinned " +
            $"{ExpectedLifetimePmIncome:0.00}. Intended-until-C1 (ADR-018 equity-basis fees) — a " +
            "deliberate fee-basis change must move the pin in the same commit; anything else is an " +
            "engine regression.");

        await AssertHeldAsync(db, OperatingTrustId, ExpectedHeldOperatingEnd, "end-state operating held fees", ct);
        await AssertHeldAsync(db, DepositTrustId, ExpectedHeldDepositEnd, "end-state deposit-trust held fees", ct);

        var violations = await sp.GetRequiredService<IInvariantChecks>().CheckCoreAsync(ct);
        Assert(violations.Count == 0,
            "scenario invariant sweep: " + string.Join("; ", violations.Select(v => $"{v.Invariant}: {v.Detail}")));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>Opens an embedded scenario-fixture CSV (committed under <c>seed/scenario-fixture/</c>).</summary>
    private static Stream OpenFixture(string fileName)
    {
        var assembly = typeof(ScenarioSeeder).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith($".{fileName}", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded scenario fixture '{fileName}' not found — is seed/scenario-fixture/*.csv " +
                "still wired as EmbeddedResource in LeaseBook.Web.csproj?");
        return assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded scenario fixture '{fileName}' failed to open.");
    }
}
