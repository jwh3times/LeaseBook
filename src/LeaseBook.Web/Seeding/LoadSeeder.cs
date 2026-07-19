using System.Text.Json;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Operations.Runs;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
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
/// Provisions the synthetic <b>load</b> org — a ~300-unit performance fixture used to size queries,
/// indexes and page budgets against something bigger than the 20-unit demo org (M8 / WP-9).
///
/// <para>
/// Shape: 25 owners, 40 properties, 300 units at ~95 % occupancy (285 active leases), and twelve
/// months of activity from <b>July 2025 through June 2026</b> inclusive. Rents run $900–$2,600 and
/// owner management fees 700–1000 bps. Three bank accounts mirror the demo org's purposes: an
/// operating Trust, a Deposit trust, and the PM's own Operating account.
/// </para>
///
/// <para>
/// <b>Everything is generated through the real engine.</b> Nothing is written to
/// <c>journal_entries</c>/<c>journal_lines</c> directly:
/// <list type="bullet">
///   <item>Chart of accounts: <see cref="IChartOfAccounts.ProvisionAsync"/>.</item>
///   <item>Opening positions: <see cref="IBalanceForward"/> (dated 2025-06-30, ties per bank by
///     construction — bank debit == the positions it backs).</item>
///   <item>Rent, late fees and owner disbursements: the bulk-run engine
///     (<see cref="RunEngine"/> preview → confirm), which is exactly what a PM operator drives.</item>
///   <item>Payments, deposits, prepayment applications, recharges, credits and the fee sweep:
///     <see cref="IAccountingEvents.PostAsync"/> with the business-event catalog.</item>
///   <item>Reconciliations: the real <see cref="StartReconciliation"/> /
///     <see cref="FinalizeReconciliation"/> commands. Months 1–11 are finalized in chronological
///     order (finalize locks the (account, month) against further bank postings); <b>June 2026 is
///     left open</b>, so the fixture has a realistic current month with uncleared items.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Determinism.</b> The fixture is generated from a fixed seed through <see cref="LoadRandom"/>,
/// a tiny xorshift64* PRNG defined in this file. <see cref="System.Random"/> is deliberately not used:
/// its sequence is not contractually stable across .NET versions, so the same seed could produce a
/// different fixture on a different runtime. Entity ids still come from <see cref="UuidV7.NewId"/>
/// (per repo convention); only the org and the three bank accounts are stable anchors.
/// </para>
///
/// <para>
/// Idempotent — safe to re-run. The journal is the anchor: if any entry exists for the org the
/// org-scoped work is skipped wholesale, so a second run is a clean no-op. Does NOT touch the demo or
/// cutover orgs, the golden dataset, or any golden figure — this is a separate, disposable org.
/// </para>
///
/// <para>
/// <b>Known fixture artifact:</b> the disbursement run assesses the management fee on
/// equity-at-run-time (ADR-018 Phase 1), so each month's fee is computed on the owner's whole cash
/// equity including the retained reserve, not just that month's collections. That is the engine's
/// documented behavior, reproduced faithfully here rather than worked around.
/// </para>
/// </summary>
public static class LoadSeeder
{
    /// <summary>Stable id for the load org — deterministic so re-runs upsert rather than duplicate.</summary>
    public static readonly Guid LoadOrgId = new("01923000-0000-7000-8000-00000010ad00");

    public const string AdminEmail = "admin@load.test";

    /// <summary>DEV ONLY documented seed password — real environments provision via Key Vault / invite.</summary>
    public const string AdminPassword = "Load-Trust-2026!";

    /// <summary>Stable bank-account ids so the chart-of-accounts provisioning is idempotent.</summary>
    public static readonly Guid OperatingTrustId = new("01923000-0000-7000-8000-000010adba01");
    public static readonly Guid DepositTrustId = new("01923000-0000-7000-8000-000010adba02");
    public static readonly Guid PmOperatingId = new("01923000-0000-7000-8000-000010adba03");

    private const string ProvisionAuditEntityType = "org-provisioned";

    // ── Fixture dimensions (see the class summary) ───────────────────────────
    private const ulong FixtureSeed = 0x10AD_5EED_2026_0001UL;
    private const int OwnerCount = 25;
    private const int PropertyCount = 40;
    private const int UnitCount = 300;
    private const int OccupiedUnitCount = 285;
    private const int MonthCount = 12;

    /// <summary>Cutover date for the opening positions — the day before the activity window opens.</summary>
    private static readonly DateOnly OpeningDate = new(2025, 6, 30);

    /// <summary>First month of generated activity; eleven more follow (through June 2026).</summary>
    private static readonly DateOnly WindowStart = new(2025, 7, 1);

    /// <summary>The PM's own accumulated (already swept) income at cutover — outside the trust equation.</summary>
    private const decimal OpeningPmOperatingBalance = 18_500.00m;

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        // Refuse to provision the well-known load admin credential in Production (account-takeover risk).
        SeederGuard.RequireNonProduction(services);

        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // Roles must exist before the admin is assigned one (idempotent).
        await RoleSeeder.EnsureRolesAsync(sp, ct);

        // Step 1 (global-class): the org itself — it has no org_id, so no context is needed.
        await EnsureOrgAsync(sp.GetRequiredService<AppDbContext>(), ct);

        // Step 2 (identity-class): the admin, bound to the org. Identity tables carry no RLS.
        await EnsureAdminAsync(sp.GetRequiredService<UserManager<AppUser>>(), ct);

        // Step 3 (org-scoped): runs inside the OrgScopedExecutor unit of work — app.org_id is set, so
        // every write passes RLS WITH CHECK. Directory rows are materialised BEFORE any posting so
        // every journal-dimension FK (P38 / ADR-008) has a target the moment its line posts.
        var executor = sp.GetRequiredService<OrgScopedExecutor>();
        var db = sp.GetRequiredService<AppDbContext>();
        await executor.RunAsync(LoadOrgId, async () =>
        {
            if (await db.Set<JournalEntry>().AnyAsync(ct))
            {
                return; // already seeded (idempotent — the journal is the anchor)
            }

            if (!await db.AuditEvents.AnyAsync(e => e.EntityType == ProvisionAuditEntityType, ct))
            {
                db.AuditEvents.Add(new AuditEvent
                {
                    Id = UuidV7.NewId(),
                    EntityType = ProvisionAuditEntityType,
                    EntityId = LoadOrgId,
                    Action = "seed",
                    After = JsonSerializer.Serialize(new { org = "load", units = UnitCount, months = MonthCount }),
                    OccurredAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }

            var rng = new LoadRandom(FixtureSeed);
            var fixture = BuildFixture(rng);

            await WriteDirectoryAsync(db, fixture, ct);
            await ProvisionChartOfAccountsAsync(sp, ct);
            await PostOpeningPositionsAsync(sp, fixture, ct);
            await PostTwelveMonthsAsync(sp, db, fixture, rng, ct);
        }, ct);
    }

    private static async Task EnsureOrgAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Orgs.AnyAsync(o => o.Id == LoadOrgId, ct))
        {
            return;
        }

        db.Orgs.Add(new Org { Id = LoadOrgId, Name = "Load Test Property Group" });
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
            OrgId = LoadOrgId,
            DisplayName = "Load Admin",
        };

        var created = await userManager.CreateAsync(admin, AdminPassword);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to seed the load admin: " + string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        await userManager.AddToRoleAsync(admin, Roles.PMAdmin);
    }

    // ── Fixture generation ───────────────────────────────────────────────────

    /// <summary>
    /// Materialises the whole fixture plan in memory (owners → properties → units → leases) before a
    /// single row is written, so unit counts, occupancy and rents are all decided by one deterministic
    /// pass of the PRNG.
    /// </summary>
    private static LoadFixture BuildFixture(LoadRandom rng)
    {
        var owners = new List<OwnerSpec>(OwnerCount);
        for (var i = 0; i < OwnerCount; i++)
        {
            // 700–1000 bps in 50 bps steps, centered on the 800 bps house default.
            var bps = 700 + (rng.Next(0, 7) * 50);
            var reserve = 250m * rng.Next(1, 5);                 // 250 / 500 / 750 / 1000
            var openingEquity = reserve + (100m * rng.Next(0, 16)); // reserve + 0–1,500
            var name = OwnerNames[i];
            owners.Add(new OwnerSpec(
                UuidV7.NewId(), name, Initials(name), bps, reserve, openingEquity));
        }

        // Every owner gets at least one property; the remaining 15 are dealt out deterministically.
        var properties = new List<PropertySpec>(PropertyCount);
        for (var i = 0; i < PropertyCount; i++)
        {
            var owner = i < OwnerCount ? owners[i] : owners[rng.Next(0, OwnerCount)];
            var address = $"{100 + (rng.Next(0, 90) * 10)} {StreetNames[i % StreetNames.Length]} "
                + StreetTypes[rng.Next(0, StreetTypes.Length)];
            properties.Add(new PropertySpec(
                UuidV7.NewId(), owner.Id, address, Cities[rng.Next(0, Cities.Length)]));
        }

        // Unit counts per property: 2–13 each, then nudged so the portfolio totals exactly 300.
        var unitCounts = new int[PropertyCount];
        for (var i = 0; i < PropertyCount; i++)
        {
            unitCounts[i] = 2 + rng.Next(0, 12);
        }

        BalanceUnitCounts(unitCounts, UnitCount);

        var units = new List<UnitSpec>(UnitCount);
        for (var p = 0; p < PropertyCount; p++)
        {
            for (var u = 0; u < unitCounts[p]; u++)
            {
                // $900–$2,600 in $25 steps.
                var rent = 900m + (25m * rng.Next(0, 69));
                units.Add(new UnitSpec(
                    UuidV7.NewId(), properties[p].Id, properties[p].OwnerId,
                    $"#{(u / 4) + 1}{(char)('A' + (u % 4))}", rent, Occupied: true));
            }
        }

        // 15 vacant units, spread evenly through the portfolio rather than clustered at the end.
        var vacancyStride = UnitCount / (UnitCount - OccupiedUnitCount);
        for (var i = 0; i < UnitCount - OccupiedUnitCount; i++)
        {
            var index = (i * vacancyStride) + rng.Next(0, vacancyStride);
            units[index] = units[index] with { Occupied = false };
        }

        // One tenant + one active lease per occupied unit. Most predate the window (their deposit is an
        // opening position); a minority start inside it, so DepositCollected and mid-month proration
        // both get exercised.
        var leases = new List<LeaseSpec>(OccupiedUnitCount);
        var newLeaseBudget = 25;
        foreach (var unit in units.Where(u => u.Occupied))
        {
            var tenantName = $"{FirstNames[rng.Next(0, FirstNames.Length)]} {LastNames[rng.Next(0, LastNames.Length)]}";
            var startsInWindow = newLeaseBudget > 0 && rng.Next(0, 100) < 10;

            DateOnly start;
            if (startsInWindow)
            {
                newLeaseBudget--;
                // Months 1–10 of the window (never the first or last month), 1st or 15th.
                var monthOffset = 1 + rng.Next(0, 10);
                var anchor = WindowStart.AddMonths(monthOffset);
                start = new DateOnly(anchor.Year, anchor.Month, rng.Next(0, 2) == 0 ? 1 : 15);
            }
            else
            {
                // Somewhere in the two years before the window opens.
                start = OpeningDate.AddDays(-(30 + rng.Next(0, 700)));
            }

            leases.Add(new LeaseSpec(
                LeaseId: UuidV7.NewId(),
                TenantId: UuidV7.NewId(),
                UnitId: unit.Id,
                PropertyId: unit.PropertyId,
                OwnerId: unit.OwnerId,
                TenantName: tenantName,
                Rent: unit.Rent,
                Start: start,
                // Every lease runs past the window so occupancy stays stable across all twelve months.
                End: new DateOnly(2026, 7, 1).AddDays(rng.Next(0, 540)),
                HasOpeningDeposit: !startsInWindow));
        }

        return new LoadFixture(owners, properties, units, leases);
    }

    /// <summary>
    /// Nudges per-property unit counts (±1 at a time, never below 1) until they sum to
    /// <paramref name="target"/>. Deterministic: it always walks the array in order.
    /// </summary>
    private static void BalanceUnitCounts(int[] counts, int target)
    {
        var index = 0;
        while (counts.Sum() != target)
        {
            var delta = counts.Sum() < target ? 1 : -1;
            if (counts[index] + delta >= 1)
            {
                counts[index] += delta;
            }

            index = (index + 1) % counts.Length;
        }
    }

    // ── Directory rows (FK targets for every journal dimension) ──────────────

    private static async Task WriteDirectoryAsync(AppDbContext db, LoadFixture fixture, CancellationToken ct)
    {
        db.Set<OrgSettings>().Add(new OrgSettings
        {
            Id = UuidV7.NewId(),
            AccountingBasis = AccountingBasis.Cash,
            MoneyNegativeDisplay = MoneyNegativeDisplay.Minus,
            LegalName = "Load Test Property Group",
            City = "Charlotte",
            State = "NC",
        });

        db.Set<BankAccount>().AddRange(
            new BankAccount
            {
                Id = OperatingTrustId,
                Name = "Load Operating Trust",
                Institution = "First Citizens",
                Mask = "5101",
                Purpose = DirectoryBankPurpose.Trust,
            },
            new BankAccount
            {
                Id = DepositTrustId,
                Name = "Load Security Deposit Trust",
                Institution = "First Citizens",
                Mask = "5102",
                Purpose = DirectoryBankPurpose.Deposit,
            },
            new BankAccount
            {
                Id = PmOperatingId,
                Name = "Load PM Operating",
                Institution = "Wells Fargo",
                Mask = "5103",
                Purpose = DirectoryBankPurpose.Operating,
            });

        foreach (var owner in fixture.Owners)
        {
            db.Set<Owner>().Add(new Owner
            {
                Id = owner.Id,
                Name = owner.Name,
                Initials = owner.Initials,
                DefaultMgmtFeeBps = owner.MgmtFeeBps,
                ReserveAmount = new Money(owner.Reserve),
            });
        }

        foreach (var property in fixture.Properties)
        {
            db.Set<Property>().Add(new Property
            {
                Id = property.Id,
                OwnerId = property.OwnerId,
                Address = property.Address,
                City = property.City,
                State = "NC",
            });
        }

        foreach (var unit in fixture.Units)
        {
            db.Set<Unit>().Add(new Unit
            {
                Id = unit.Id,
                PropertyId = unit.PropertyId,
                Label = unit.Label,
                Rent = new Money(unit.Rent),
                Status = unit.Occupied ? UnitStatus.Occupied : UnitStatus.Vacant,
            });
        }

        foreach (var lease in fixture.Leases)
        {
            db.Set<Tenant>().Add(new Tenant
            {
                Id = lease.TenantId,
                DisplayName = lease.TenantName,
                Status = TenantStatus.Current,
            });

            db.Set<LeaseLite>().Add(new LeaseLite
            {
                Id = lease.LeaseId,
                TenantId = lease.TenantId,
                UnitId = lease.UnitId,
                StartDate = lease.Start,
                EndDate = lease.End,
                Rent = new Money(lease.Rent),
                DepositRequired = new Money(lease.Rent),
                Status = LeaseStatus.Active,
            });
        }

        // One SaveChanges flushes the whole directory into the ambient org transaction, so it is visible
        // to the postings that follow in the same transaction and their dimension FKs resolve.
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private static Task ProvisionChartOfAccountsAsync(IServiceProvider sp, CancellationToken ct) =>
        sp.GetRequiredService<IChartOfAccounts>().ProvisionAsync(
            [
                new BankAccountSpec(OperatingTrustId, "Load Operating Trust", AccountingBankPurpose.Trust),
                new BankAccountSpec(DepositTrustId, "Load Security Deposit Trust", AccountingBankPurpose.Deposit),
                new BankAccountSpec(PmOperatingId, "Load PM Operating", AccountingBankPurpose.Operating),
            ], ct);

    // ── Opening positions ────────────────────────────────────────────────────

    /// <summary>
    /// Posts the cutover positions through <see cref="IBalanceForward"/>. The entry ties per bank by
    /// construction: each bank's debit is the exact sum of the credits carrying that bank's dimension,
    /// so the trust equation holds the moment the fixture opens.
    /// </summary>
    private static Task PostOpeningPositionsAsync(IServiceProvider sp, LoadFixture fixture, CancellationToken ct)
    {
        var lines = new List<BalanceForwardLine>();

        // Operating trust: book == Σ owner equity.
        var ownerEquityTotal = fixture.Owners.Sum(o => o.OpeningEquity);
        lines.Add(new BalanceForwardLine(
            AccountCodes.TrustBank(OperatingTrustId), new Money(ownerEquityTotal), null,
            BankAccountId: OperatingTrustId));
        foreach (var owner in fixture.Owners)
        {
            lines.Add(new BalanceForwardLine(
                AccountCodes.OwnerEquity, null, new Money(owner.OpeningEquity),
                OwnerId: owner.Id, BankAccountId: OperatingTrustId));
        }

        // Security-deposit trust: book == Σ deposits held for leases that predate the window. Leases
        // that start inside the window collect their deposit through DepositCollected instead.
        var carriedDeposits = fixture.Leases.Where(l => l.HasOpeningDeposit).ToList();
        lines.Add(new BalanceForwardLine(
            AccountCodes.TrustBank(DepositTrustId), new Money(carriedDeposits.Sum(l => l.Rent)), null,
            BankAccountId: DepositTrustId));
        foreach (var lease in carriedDeposits)
        {
            lines.Add(new BalanceForwardLine(
                AccountCodes.SecurityDepositsHeld, null, new Money(lease.Rent),
                PropertyId: lease.PropertyId, OwnerId: lease.OwnerId, TenantId: lease.TenantId,
                BankAccountId: DepositTrustId));
        }

        // PM operating: already-swept PM income, outside the trust equation (not a trust_bank).
        lines.Add(new BalanceForwardLine(
            AccountCodes.PmOperatingBank(PmOperatingId), new Money(OpeningPmOperatingBalance), null,
            BankAccountId: PmOperatingId));
        lines.Add(new BalanceForwardLine(
            AccountCodes.PmIncome, null, new Money(OpeningPmOperatingBalance),
            BankAccountId: PmOperatingId));

        return sp.GetRequiredService<IBalanceForward>().PostAsync(
            new BalanceForwardRequest(OpeningDate, lines, "Load fixture opening positions"), ct);
    }

    // ── Twelve months of activity ────────────────────────────────────────────

    /// <summary>
    /// Walks the window month by month in chronological order. Each month: collect deposits for leases
    /// starting that month, run rent, apply carried prepayments, collect payments, post a handful of
    /// recharges/credits, run late fees, run owner disbursements, sweep the PM's held fees, then clear
    /// and finalize each bank's reconciliation. The final month (June 2026) is deliberately left
    /// unreconciled — finalize locks the period, and an open current month is what a live org looks like.
    /// </summary>
    private static async Task PostTwelveMonthsAsync(
        IServiceProvider sp, AppDbContext db, LoadFixture fixture, LoadRandom rng, CancellationToken ct)
    {
        var events = sp.GetRequiredService<IAccountingEvents>();
        var engine = sp.GetRequiredService<RunEngine>();
        var sender = sp.GetRequiredService<ISender>();

        var leaseById = fixture.Leases.ToDictionary(l => l.LeaseId);

        // Exact per-lease shadow ledger. Every receivable-moving posting in this seeder is either
        // issued here or read back from a run preview, so `owed` stays in lock-step with the engine's
        // own tenant_receivable balance — which is what makes the payment auto-split (P31) and the
        // prepayment/credit guards predictable instead of racy.
        var owed = fixture.Leases.ToDictionary(l => l.LeaseId, _ => 0m);
        var prepaid = fixture.Leases.ToDictionary(l => l.LeaseId, _ => 0m);

        for (var m = 0; m < MonthCount; m++)
        {
            var monthStart = WindowStart.AddMonths(m);
            var period = new RunPeriod(monthStart.Year, monthStart.Month);
            var daysInMonth = DateTime.DaysInMonth(period.Year, period.Month);
            var monthEnd = new DateOnly(period.Year, period.Month, daysInMonth);

            DateOnly Day(int day) => new(period.Year, period.Month, Math.Min(day, daysInMonth));

            // 1. Deposits for leases starting this month (liability, never income).
            foreach (var lease in fixture.Leases.Where(l =>
                !l.HasOpeningDeposit && l.Start.Year == period.Year && l.Start.Month == period.Month))
            {
                await events.PostAsync(new DepositCollected(
                    lease.TenantId, lease.PropertyId, lease.OwnerId, new Money(lease.Rent),
                    lease.Start, DepositTrustId, $"Security deposit — {lease.TenantName}"), ct);
            }

            // 2. Rent run (bulk-run engine). Proration is the strategy's job for mid-month starts.
            var rentRows = await ConfirmRunAsync(engine, RunType.Rent, period, ct);
            foreach (var row in rentRows)
            {
                owed[row.TargetId] += row.Amount;
            }

            db.ChangeTracker.Clear();

            // 3. Apply prepayments carried in from last month's overpayers (liability → income).
            foreach (var (leaseId, held) in prepaid.Where(p => p.Value > 0m).ToList())
            {
                var lease = leaseById[leaseId];
                var apply = Math.Min(held, owed[leaseId]);
                if (apply <= 0m)
                {
                    continue;
                }

                await events.PostAsync(new PrepaymentApplied(
                    lease.TenantId, lease.PropertyId, lease.OwnerId, new Money(apply),
                    Day(2), OperatingTrustId, $"Prepayment applied — {period.Key}"), ct);
                prepaid[leaseId] -= apply;
                owed[leaseId] -= apply;
            }

            // 4. Tenant payments: ~93 % of charged leases pay, split across on-time, late-but-full,
            //    partial, and a few deliberate overpayments (which become prepayment liabilities).
            var posted = 0;
            foreach (var row in rentRows)
            {
                var lease = leaseById[row.TargetId];
                var balance = owed[row.TargetId];
                var roll = rng.Next(0, 100);

                decimal amount;
                int day;
                if (roll < 4)
                {
                    continue; // missed the month entirely — feeds the late-fee run
                }
                else if (roll < 7)
                {
                    amount = Round2(row.Amount * (0.5m + (0.01m * rng.Next(0, 30)))); // partial
                    day = 6 + rng.Next(0, 10);
                }
                else if (roll < 9)
                {
                    amount = balance + (25m * rng.Next(1, 9)); // overpay → prepayment liability
                    day = 2 + rng.Next(0, 4);
                }
                else if (roll < 20)
                {
                    amount = Math.Min(balance, row.Amount); // late, but clears before month end
                    day = 12 + rng.Next(0, 14);
                }
                else
                {
                    // On time. Tenants carrying arrears often catch the whole thing up.
                    amount = balance > row.Amount && rng.Next(0, 100) < 60
                        ? balance
                        : Math.Min(balance, row.Amount);
                    day = 2 + rng.Next(0, 5);
                }

                if (amount <= 0m)
                {
                    continue;
                }

                await events.PostAsync(new PaymentReceived(
                    lease.TenantId, lease.PropertyId, lease.OwnerId, new Money(amount),
                    Day(day), PaymentMethods[rng.Next(0, PaymentMethods.Length)], OperatingTrustId,
                    $"Payment — {lease.TenantName} {period.Key}"), ct);

                // Mirror the engine's auto-split: up to the open receivable clears it, the rest is a
                // prepayment. Never a negative receivable.
                var applied = Math.Min(amount, Math.Max(balance, 0m));
                owed[row.TargetId] = balance - applied;
                prepaid[row.TargetId] += amount - applied;

                if (++posted % 100 == 0)
                {
                    // EF change tracking degrades badly with tens of thousands of tracked entities and
                    // DetectChanges runs on every SaveChanges. Everything above is already committed to
                    // the ambient transaction, and the posting services hold no tracked state between
                    // calls, so periodic clearing is safe and keeps the run roughly linear.
                    db.ChangeTracker.Clear();
                }
            }

            db.ChangeTracker.Clear();

            // 5. A handful of one-off recharges (mostly paid a week later) and goodwill credits.
            for (var i = 0; i < 5 && rentRows.Count > 0; i++)
            {
                var row = rentRows[rng.Next(0, rentRows.Count)];
                var lease = leaseById[row.TargetId];
                var amount = 45m + (5m * rng.Next(0, 30));

                await events.PostAsync(new FeeCharged(
                    lease.TenantId, lease.PropertyId, lease.OwnerId, lease.UnitId, new Money(amount),
                    Day(10), FeeKind.MaintenanceRecharge, $"Recharge — {lease.TenantName} {period.Key}"), ct);
                owed[row.TargetId] += amount;

                if (rng.Next(0, 100) < 70)
                {
                    await events.PostAsync(new PaymentReceived(
                        lease.TenantId, lease.PropertyId, lease.OwnerId, new Money(amount),
                        Day(20), PaymentMethod.Ach, OperatingTrustId,
                        $"Recharge payment — {lease.TenantName} {period.Key}"), ct);
                    owed[row.TargetId] -= amount;
                }
            }

            // Credits only go to leases that still owe at least the credit, so the receivable is never
            // driven negative behind the engine's back.
            for (var i = 0; i < 3 && rentRows.Count > 0; i++)
            {
                var row = rentRows[rng.Next(0, rentRows.Count)];
                var lease = leaseById[row.TargetId];
                var amount = 25m + (5m * rng.Next(0, 16));
                if (owed[row.TargetId] < amount)
                {
                    continue;
                }

                await events.PostAsync(new CreditIssued(
                    lease.TenantId, lease.PropertyId, lease.OwnerId, new Money(amount),
                    Day(14), $"Goodwill credit — {lease.TenantName} {period.Key}"), ct);
                owed[row.TargetId] -= amount;
            }

            db.ChangeTracker.Clear();

            // 6. Late-fee run (bulk-run engine, NC §42-46 clamp) over whoever still owes at month end.
            var lateFeeRows = await ConfirmRunAsync(engine, RunType.LateFee, period, ct);
            foreach (var row in lateFeeRows)
            {
                owed[row.TargetId] += row.Amount;
            }

            db.ChangeTracker.Clear();

            // 7. Owner disbursement run (bulk-run engine) — folds the management fee in (ADR-018).
            await ConfirmRunAsync(engine, RunType.Disbursement, period, ct);
            db.ChangeTracker.Clear();

            // 8. Sweep the PM fees now held in the operating trust to the PM's own bank.
            var heldFees = await HeldPmFeesAsync(db, ct);
            if (heldFees > 0m)
            {
                await events.PostAsync(new PMFeesSwept(
                    new Money(heldFees), Day(28), OperatingTrustId, PmOperatingId,
                    $"Mgmt fee transfer → PM Operating — {period.Key}"), ct);
            }

            db.ChangeTracker.Clear();

            // 9. Reconcile and lock every bank for every month but the last. Finalize locks the
            //    (account, month) against further bank postings, which is why this runs after all of
            //    the month's postings and why the months are walked in order.
            if (m < MonthCount - 1)
            {
                foreach (var bankId in new[] { OperatingTrustId, DepositTrustId, PmOperatingId })
                {
                    await ReconcileAndFinalizeAsync(db, sender, bankId, period, monthEnd, ct);
                }

                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// Previews a run, selects every eligible row (not already done, not excluded, non-zero), confirms
    /// it, and hands back the rows that were selected so the caller can mirror their effect. Returns an
    /// empty list when the run has nothing to do.
    /// </summary>
    private static async Task<IReadOnlyList<PreviewRow>> ConfirmRunAsync(
        RunEngine engine, RunType runType, RunPeriod period, CancellationToken ct)
    {
        var preview = await engine.PreviewAsync(runType, period, ct);
        var selected = preview.Rows
            .Where(r => !r.AlreadyDone && r.ExcludedReason is null && r.Amount > 0m)
            .ToList();

        if (selected.Count == 0)
        {
            return [];
        }

        await engine.ConfirmAsync(runType, period, selected.Select(r => r.TargetId).ToList(), ct);
        return selected;
    }

    /// <summary>
    /// PM fees currently held in the operating trust bank (the sweepable balance). Read here rather
    /// than tracked in memory because the disbursement run — not this seeder — decides the fee amounts.
    /// </summary>
    private static async Task<decimal> HeldPmFeesAsync(AppDbContext db, CancellationToken ct) =>
        (await db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(COALESCE(credit, 0) - COALESCE(debit, 0)), 0) AS "Value"
            FROM journal_lines
            WHERE account_class = 'pm_income' AND bank_account_id = {OperatingTrustId}
              AND basis IN ('cash', 'both')
            """).ToListAsync(ct)).Single();

    /// <summary>
    /// Marks every one of the account's bank lines through <paramref name="monthEnd"/> cleared, then
    /// drives the real reconciliation commands to a $0.00 difference and finalizes. The statement
    /// balance is read back from the engine's own <see cref="ReconciliationView.ClearedBalance"/>
    /// rather than recomputed here, so the fixture can never disagree with the reconciliation report.
    /// <para>
    /// Clearance lives on the <c>bank_line_status</c> side table (P62), never on a journal row, so this
    /// is written as a raw upsert exactly like <see cref="DemoBankClearingSeed"/> and the
    /// ApplyClearances command — not through EF change tracking.
    /// </para>
    /// </summary>
    private static async Task ReconcileAndFinalizeAsync(
        AppDbContext db, ISender sender, Guid bankId, RunPeriod period, DateOnly monthEnd, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO bank_line_status (journal_line_id, org_id, status, cleared_at, created_at, updated_at)
            SELECT jl.id, {LoadOrgId}, 'cleared', now(), now(), now()
            FROM journal_lines jl
            JOIN journal_entries e ON e.id = jl.entry_id
            WHERE jl.bank_account_id = {bankId}
              AND jl.account_class IN ('trust_bank', 'pm_operating_bank')
              AND jl.basis IN ('cash', 'both')
              AND e.entry_date <= {monthEnd}
            ON CONFLICT (journal_line_id) DO NOTHING
            """, ct);

        // Open with a placeholder, read the live cleared balance back, restate the statement to match
        // (StartReconciliation on an open reconciliation updates it), then finalize at a $0 difference.
        var opened = await sender.Send(new StartReconciliation(bankId, period.Year, period.Month, 0m), ct);
        var restated = await sender.Send(
            new StartReconciliation(bankId, period.Year, period.Month, opened.ClearedBalance), ct);
        await sender.Send(new FinalizeReconciliation(restated.Id), ct);
    }

    private static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? $"{parts[0][0]}{parts[1][0]}"
            : name[..Math.Min(2, name.Length)].ToUpperInvariant();
    }

    // ── Deterministic vocabulary ─────────────────────────────────────────────

    private static readonly PaymentMethod[] PaymentMethods =
        [PaymentMethod.Ach, PaymentMethod.Ach, PaymentMethod.Card, PaymentMethod.Check, PaymentMethod.Cash];

    private static readonly string[] Cities =
        ["Charlotte", "Raleigh", "Durham", "Greensboro", "Winston-Salem", "Cary", "Concord", "Gastonia"];

    private static readonly string[] StreetTypes =
        ["Ave", "St", "Rd", "Dr", "Ln", "Ct", "Blvd", "Way"];

    private static readonly string[] StreetNames =
    [
        "Oakmont", "Riverside", "Charlotte", "Westwood", "Haywood", "Sunset", "Ivy Creek", "Providence",
        "Sardis", "Monroe", "Tryon", "Queens", "Selwyn", "Kenilworth", "Dilworth", "Elizabeth",
        "Plaza", "Central", "Commonwealth", "Thomas", "Pecan", "Clement", "Louise", "Belmont",
        "Parkwood", "Davidson", "Brevard", "Caldwell", "Myers", "Colville", "Hertford", "Wendover",
        "Lassiter", "Ardsley", "Cherokee", "Roswell", "Hopedale", "Malvern", "Granville", "Runnymede",
    ];

    private static readonly string[] OwnerNames =
    [
        "Hollis Family Trust", "Piedmont Holdings LLC", "Arden & Kate Beckwith", "Delia Marchetti",
        "Summit Ridge Investments", "The Adebayo Group", "Rosalind Tanner", "Beacon Row Partners",
        "Catawba Property Co", "Lorenzo Vance", "Harper Ridge LLC", "Nadia Okonjo",
        "Stonecrest Capital", "Gideon Alvarez", "Blue Ridge Rentals LLC", "Marguerite Doyle",
        "Halstead Trust", "Uptown Yield Partners", "Teodoro Salinas", "Wren Hollow LLC",
        "Cassandra Whitlock", "Ironwood Estates", "Priya Raghunathan", "Fairhaven Group",
        "Desmond Kirkpatrick",
    ];

    private static readonly string[] FirstNames =
    [
        "Jasmine", "Devon", "Aisha", "Cole", "Lena", "Brandon", "Marcus", "Priya", "Tobias", "Elena",
        "Omar", "Sierra", "Nathan", "Camille", "Isaiah", "Rosa", "Grant", "Yara", "Silas", "Naomi",
        "Preston", "Imani", "Julian", "Fiona", "Andre", "Marisol", "Corey", "Delphine", "Emmett", "Wren",
    ];

    private static readonly string[] LastNames =
    [
        "Carter", "Pryor", "Bello", "Ramsey", "Vasquez", "Tate", "Okonkwo", "Liu", "Whitfield", "Nunez",
        "Hargrove", "Bell", "Okafor", "Calloway", "Mercer", "Sandoval", "Ferris", "Brannigan", "Dockery",
        "Vaughn", "Ashworth", "Kimura", "Delacroix", "Ferreira", "Strand", "Mbeki", "Rowntree", "Salgado",
    ];

    // ── Fixture model ────────────────────────────────────────────────────────

    private sealed record LoadFixture(
        IReadOnlyList<OwnerSpec> Owners,
        IReadOnlyList<PropertySpec> Properties,
        IReadOnlyList<UnitSpec> Units,
        IReadOnlyList<LeaseSpec> Leases);

    private sealed record OwnerSpec(
        Guid Id, string Name, string Initials, int MgmtFeeBps, decimal Reserve, decimal OpeningEquity);

    private sealed record PropertySpec(Guid Id, Guid OwnerId, string Address, string City);

    private sealed record UnitSpec(
        Guid Id, Guid PropertyId, Guid OwnerId, string Label, decimal Rent, bool Occupied);

    private sealed record LeaseSpec(
        Guid LeaseId, Guid TenantId, Guid UnitId, Guid PropertyId, Guid OwnerId,
        string TenantName, decimal Rent, DateOnly Start, DateOnly End, bool HasOpeningDeposit);

    /// <summary>
    /// A tiny xorshift64* PRNG. Deliberately hand-rolled: <see cref="System.Random"/>'s sequence is an
    /// implementation detail that is not contractually stable across .NET versions, so seeding it would
    /// not guarantee the same fixture on every machine and runtime. This algorithm is fixed here, in
    /// source, forever — same seed in, byte-identical fixture out.
    /// </summary>
    private sealed class LoadRandom(ulong seed)
    {
        private ulong _state = seed == 0UL ? 0x9E37_79B9_7F4A_7C15UL : seed;

        /// <summary>A uniformly distributed integer in <c>[minInclusive, maxExclusive)</c>.</summary>
        public int Next(int minInclusive, int maxExclusive) =>
            minInclusive + (int)(NextUInt64() % (ulong)(maxExclusive - minInclusive));

        private ulong NextUInt64()
        {
            var x = _state;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            _state = x;
            return x * 0x2545_F491_4F6C_DD1DUL;
        }
    }
}
