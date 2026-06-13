using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Seeding;

/// <summary>
/// Materialises the demo org's directory rows (owners, properties, tenants, bank accounts, settings)
/// reusing the exact <see cref="DemoIds"/> GUIDs the M1 journal already carries as dimensions (P26).
/// Runs <b>before</b> <see cref="DemoJournalSeed"/> in the seeder so every <c>journal_lines</c> dimension
/// FK (P38 / ADR-008) has a target the moment its line posts. Idempotent — skips if owners already exist.
/// <para>
/// The synthetic aggregate ids the journal references (<c>AggregateOwners</c>, the per-owner deposit
/// aggregates, the unattributed-deposit bucket, and the statement-only tenants) are materialised as
/// <see cref="Owner.IsSystem"/> / <see cref="Tenant.IsSystem"/> rows (§C.2) so the FKs hold without any
/// journal row changing. Every list/search/CRUD surface filters <c>WHERE NOT is_system</c>.
/// </para>
/// <para>
/// Units and leases are seeded too (§C.10) — one unit per distinct tenant unit string plus filler vacant
/// units up to each property's count, and an active lease per tenant. The journal never references a unit
/// (its <c>unit_id</c> is always null), so they are not FK targets; they exist for the directory screens
/// and the dashboard vacancy / collected-target KPIs.
/// </para>
/// </summary>
internal static class DemoDirectorySeed
{
    /// <summary>Org/owner default management fee: 8% (the statement's "Management fee (8% of collected)").</summary>
    private const int DefaultMgmtFeeBps = 800;

    public static async Task SeedAsync(DbContext db, CancellationToken ct)
    {
        if (await db.Set<Owner>().AnyAsync(ct))
        {
            return; // already seeded (idempotent — owners are the anchor)
        }

        SeedOwners(db);
        SeedProperties(db);
        SeedTenants(db);
        SeedBankAccounts(db);
        SeedOrgSettings(db);
        SeedUnitsAndLeases(db);

        // One SaveChanges flushes all directory rows into the ambient org transaction. They become
        // visible to the journal replay that runs next in the same transaction, so its FKs resolve.
        await db.SaveChangesAsync(ct);
    }

    private static void SeedOwners(DbContext db)
    {
        Owner Real(Guid id, string name, string initials) => new()
        {
            Id = id,
            Name = name,
            Initials = initials,
            DefaultMgmtFeeBps = DefaultMgmtFeeBps,
        };

        db.Set<Owner>().AddRange(
            Real(DemoIds.O1, "Hargrove Family Trust", "HF"),
            Real(DemoIds.O2, "Coastal Holdings LLC", "CH"),
            Real(DemoIds.O3, "Marcus & Dana Bell", "MB"),
            Real(DemoIds.O4, "Patricia Nunez", "PN"),
            Real(DemoIds.O5, "Ridgeline Investments", "RI"),
            Real(DemoIds.O6, "The Okafor Group", "OK"),
            Real(DemoIds.O7, "Sandra Whitfield", "SW"),
            Real(DemoIds.O8, "Beacon Street Partners", "BS"),
            // The 15 unlisted owners' rolled-up equity (P40) — a system row, relabeled by the dashboard
            // hero ("All other owners (15)") and hidden everywhere else.
            new Owner { Id = DemoIds.AggregateOwners, Name = "All other owners", IsSystem = true });
    }

    private static void SeedProperties(DbContext db)
    {
        Property P(Guid id, Guid ownerId, string address, string city) => new()
        {
            Id = id,
            OwnerId = ownerId,
            Address = address,
            City = city,
            State = "NC",
        };

        db.Set<Property>().AddRange(
            P(DemoIds.P1, DemoIds.O1, "412 Oakmont Ave", "Asheville"),
            P(DemoIds.P2, DemoIds.O1, "88 Riverside Dr", "Asheville"),
            P(DemoIds.P3, DemoIds.O2, "1029 Charlotte St", "Asheville"),
            P(DemoIds.P4, DemoIds.O2, "55 Westwood Pl", "Weaverville"),
            P(DemoIds.P5, DemoIds.O5, "230 Haywood Rd", "Asheville"),
            P(DemoIds.P6, DemoIds.O4, "17 Sunset Ter", "Black Mtn"));
    }

    private static void SeedTenants(DbContext db)
    {
        db.Set<Tenant>().AddRange(
            new Tenant
            {
                Id = DemoIds.T1,
                DisplayName = "Jasmine Carter",
                Status = TenantStatus.Current,
                ContactEmail = "jcarter@email.com",
                ContactPhone = "(828) 555-0148",
            },
            new Tenant { Id = DemoIds.T2, DisplayName = "Devon Pryor", Status = TenantStatus.Current },
            new Tenant { Id = DemoIds.T3, DisplayName = "Aisha Bello", Status = TenantStatus.Late },
            new Tenant { Id = DemoIds.T4, DisplayName = "Cole Ramsey", Status = TenantStatus.Current },
            new Tenant { Id = DemoIds.T5, DisplayName = "The Mercer Family", Status = TenantStatus.Prepaid },
            new Tenant { Id = DemoIds.T6, DisplayName = "Lena Vasquez", Status = TenantStatus.Late },
            new Tenant { Id = DemoIds.T7, DisplayName = "Brandon Tate", Status = TenantStatus.Current });

        // System tenant rows backing the journal's deposit aggregates (§C.2) — FK targets only, hidden.
        Tenant System(Guid id, string label) => new() { Id = id, DisplayName = label, IsSystem = true };
        db.Set<Tenant>().AddRange(
            System(DemoIds.AggDepO1, "Deposit aggregate · Hargrove"),
            System(DemoIds.AggDepO2, "Deposit aggregate · Coastal"),
            System(DemoIds.AggDepO3, "Deposit aggregate · Bell"),
            System(DemoIds.AggDepO4, "Deposit aggregate · Nunez"),
            System(DemoIds.AggDepO5, "Deposit aggregate · Ridgeline"),
            System(DemoIds.AggDepO6, "Deposit aggregate · Okafor"),
            System(DemoIds.AggDepO7, "Deposit aggregate · Whitfield"),
            System(DemoIds.AggDepO8, "Deposit aggregate · Beacon"),
            System(DemoIds.AggregateDepositsUnattributed, "Unattributed deposits"),
            // Statement-only tenants (May statement, M5) — seeded now so the id set is complete (§C.10).
            System(DemoIds.TOkonkwo, "T. Okonkwo"),
            System(DemoIds.TLiu, "T. Liu"));
    }

    private static void SeedBankAccounts(DbContext db)
    {
        db.Set<BankAccount>().AddRange(
            new BankAccount
            {
                Id = DemoIds.OperBank,
                Name = "Operating Trust",
                Institution = "First Citizens",
                Mask = "4021",
                Purpose = BankPurpose.Trust,
            },
            new BankAccount
            {
                Id = DemoIds.DepositBank,
                Name = "Security Deposit Trust",
                Institution = "First Citizens",
                Mask = "8847",
                Purpose = BankPurpose.Deposit,
            },
            new BankAccount
            {
                Id = DemoIds.MgmtBank,
                Name = "PM Operating",
                Institution = "Wells Fargo",
                Mask = "1190",
                Purpose = BankPurpose.Operating,
            });
    }

    private static void SeedOrgSettings(DbContext db)
    {
        db.Set<OrgSettings>().Add(new OrgSettings
        {
            Id = UuidV7.NewId(),
            AccountingBasis = AccountingBasis.Cash,
            MoneyNegativeDisplay = MoneyNegativeDisplay.Minus,
            LegalName = "Tarheel Property Group",
            City = "Asheville",
            State = "NC",
        });
    }

    private static void SeedUnitsAndLeases(DbContext db)
    {
        // One unit per distinct tenant unit string (occupied), plus filler vacant units up to each
        // property's count (data.jsx: p1=4, p2=1, p3=6, p4=3, p5=4, p6=2 → 20 units, 7 occupied; §C.10).
        (Guid Property, string Label, decimal Rent, UnitStatus Status, Guid? Tenant)[] specs =
        [
            (DemoIds.P1, "#2B", 1450m, UnitStatus.Occupied, DemoIds.T1), // Jasmine Carter
            (DemoIds.P1, "#1A", 1380m, UnitStatus.Occupied, DemoIds.T2), // Devon Pryor
            (DemoIds.P1, "#3C", 1400m, UnitStatus.Vacant, null),
            (DemoIds.P1, "#4D", 1400m, UnitStatus.Vacant, null),
            (DemoIds.P2, "88 Riverside Dr", 2150m, UnitStatus.Occupied, DemoIds.T5), // The Mercer Family (whole home)
            (DemoIds.P3, "#3", 1620m, UnitStatus.Occupied, DemoIds.T3), // Aisha Bello
            (DemoIds.P3, "#5", 1550m, UnitStatus.Occupied, DemoIds.T7), // Brandon Tate
            (DemoIds.P3, "#1", 1500m, UnitStatus.Vacant, null),
            (DemoIds.P3, "#2", 1500m, UnitStatus.Vacant, null),
            (DemoIds.P3, "#4", 1500m, UnitStatus.Vacant, null),
            (DemoIds.P3, "#6", 1500m, UnitStatus.Vacant, null),
            (DemoIds.P4, "#2", 1410m, UnitStatus.Occupied, DemoIds.T6), // Lena Vasquez
            (DemoIds.P4, "#1", 1400m, UnitStatus.Vacant, null),
            (DemoIds.P4, "#3", 1400m, UnitStatus.Vacant, null),
            (DemoIds.P5, "#B", 1295m, UnitStatus.Occupied, DemoIds.T4), // Cole Ramsey
            (DemoIds.P5, "#A", 1300m, UnitStatus.Vacant, null),
            (DemoIds.P5, "#C", 1300m, UnitStatus.Vacant, null),
            (DemoIds.P5, "#D", 1300m, UnitStatus.Vacant, null),
            (DemoIds.P6, "#1", 1200m, UnitStatus.Vacant, null),
            (DemoIds.P6, "#2", 1200m, UnitStatus.Vacant, null),
        ];

        var unitByTenant = new Dictionary<Guid, (Guid UnitId, decimal Rent)>();
        foreach (var (property, label, rent, status, tenant) in specs)
        {
            var unit = new Unit
            {
                Id = UuidV7.NewId(),
                PropertyId = property,
                Label = label,
                Rent = new Money(rent),
                Status = status,
            };
            db.Set<Unit>().Add(unit);
            if (tenant is { } tenantId)
            {
                unitByTenant[tenantId] = (unit.Id, rent);
            }
        }

        // An active lease per current tenant. t1's term is the focal tenant's (data.jsx); others get a
        // standard one-year term. Deposit held = one month's rent (t1 matches the prototype's 1,450).
        foreach (var tenantId in new[] { DemoIds.T1, DemoIds.T2, DemoIds.T3, DemoIds.T4, DemoIds.T5, DemoIds.T6, DemoIds.T7 })
        {
            var (unitId, rent) = unitByTenant[tenantId];
            var start = tenantId == DemoIds.T1 ? new DateOnly(2024, 6, 1) : new DateOnly(2025, 6, 1);
            db.Set<LeaseLite>().Add(new LeaseLite
            {
                Id = UuidV7.NewId(),
                TenantId = tenantId,
                UnitId = unitId,
                StartDate = start,
                EndDate = new DateOnly(2026, 5, 31),
                Rent = new Money(rent),
                DepositRequired = new Money(rent),
                Status = LeaseStatus.Active,
            });
        }
    }
}
