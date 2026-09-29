using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Settings;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Portal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using AccountingBankPurpose = LeaseBook.Modules.Accounting.Contracts.BankPurpose;
using DirectoryBankPurpose = LeaseBook.Modules.Directory.Domain.BankPurpose;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The persona pack (#314, ADR-048) — <see cref="OrgIsolationTests"/>' counterpart one level down.
/// Every assertion runs on a raw <b>app-role</b> connection with the context set exactly as
/// <c>OrgScopedExecutor</c> sets it, so it proves what Postgres enforces inside one organization,
/// not what the portal endpoints happen to ask for.
/// <para>
/// Each test builds its own organization: two owners and two tenants, each with a portal login and
/// active link, leases, posted journal activity, an issued statement, an ownership transfer and a
/// simulated payment with its observation. Row expectations are derived from a superuser read of the
/// same org — the one connection RLS never filters — so "sees exactly its own" is compared against
/// ground truth rather than against another filtered read.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PersonaIsolationTests(PostgresFixture fixture)
{
    /// <summary>What the owner persona may read. Everything else must read as empty.</summary>
    private static readonly HashSet<string> OwnerReadable = new(StringComparer.Ordinal)
    {
        "owner_access", "owners", "journal_lines", "journal_entries", "properties",
        "statement_artifacts", "org_settings",
    };

    /// <summary>What the tenant persona may read. Everything else must read as empty.</summary>
    private static readonly HashSet<string> TenantReadable = new(StringComparer.Ordinal)
    {
        "resident_access", "tenants", "journal_lines", "journal_entries", "accounts", "lease_lite",
        "units", "properties", "property_ownership_transfers", "bank_accounts", "payment_operations",
        "payment_observations", "payment_fixtures", "org_settings",
    };

    [Fact]
    public async Task An_owner_reads_exactly_its_own_rows_in_every_granted_table()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SeedAsync(ct);

        foreach (var (user, owner, other) in new[] { (s.OwnerAUser, s.OwnerA, s.OwnerB), (s.OwnerBUser, s.OwnerB, s.OwnerA) })
        {
            await using var conn = await fixture.OpenAppConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await RlsProbe.SetOrgContextAsync(conn, tx, s.OrgId, "owner", user, ct);

            (await IdsAsync(conn, tx, "SELECT user_id FROM owner_access", ct)).ShouldBe([user]);
            (await IdsAsync(conn, tx, "SELECT id FROM owners", ct)).ShouldBe([owner]);

            var lineOwners = await IdsAsync(conn, tx, "SELECT DISTINCT owner_id FROM journal_lines", ct);
            lineOwners.ShouldBe([owner], "an owner reads only lines attributed to it");
            (await CountAsync(conn, tx, "journal_lines", ct)).ShouldBe(
                await TruthCountAsync(s.OrgId, $"SELECT count(*) FROM journal_lines WHERE org_id = @org AND owner_id = '{owner}'", ct));
            (await IdsAsync(conn, tx, "SELECT id FROM journal_entries", ct)).ShouldBe(
                await TruthIdsAsync(s.OrgId, $"SELECT DISTINCT entry_id FROM journal_lines WHERE org_id = @org AND owner_id = '{owner}'", ct),
                ignoreOrder: true);
            (await IdsAsync(conn, tx, "SELECT owner_id FROM statement_artifacts", ct)).ShouldBe([owner]);
            (await CountAsync(conn, tx, "org_settings", ct)).ShouldBe(1);

            // Properties: currently owned, or present on the owner's own lines. Owner A's history on
            // the property it has since transferred to Owner B stays readable to A; B's own property
            // is never readable to A.
            var properties = await IdsAsync(conn, tx, "SELECT id FROM properties", ct);
            if (owner == s.OwnerA)
            {
                properties.ShouldBe([s.PropertyA, s.PropertyTransferred], ignoreOrder: true);
            }
            else
            {
                properties.ShouldBe([s.PropertyB, s.PropertyTransferred], ignoreOrder: true);
            }

            properties.ShouldNotContain(owner == s.OwnerA ? s.PropertyB : s.PropertyA);
            (await IdsAsync(conn, tx, $"SELECT id FROM owners WHERE id = '{other}'", ct)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_tenant_reads_exactly_its_own_rows_in_every_granted_table()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SeedAsync(ct);

        foreach (var (user, tenant, lease, unit, property, operation) in new[]
        {
            (s.TenantAUser, s.TenantA, s.LeaseA, s.UnitA, s.PropertyA, s.OperationA),
            (s.TenantBUser, s.TenantB, s.LeaseB, s.UnitB, s.PropertyB, s.OperationB),
        })
        {
            await using var conn = await fixture.OpenAppConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await RlsProbe.SetOrgContextAsync(conn, tx, s.OrgId, "tenant", user, ct);

            (await IdsAsync(conn, tx, "SELECT user_id FROM resident_access", ct)).ShouldBe([user]);
            (await IdsAsync(conn, tx, "SELECT id FROM tenants", ct)).ShouldBe([tenant]);
            (await IdsAsync(conn, tx, "SELECT DISTINCT tenant_id FROM journal_lines", ct)).ShouldBe([tenant]);
            (await CountAsync(conn, tx, "journal_lines", ct)).ShouldBe(
                await TruthCountAsync(s.OrgId, $"SELECT count(*) FROM journal_lines WHERE org_id = @org AND tenant_id = '{tenant}'", ct));
            (await IdsAsync(conn, tx, "SELECT id FROM journal_entries", ct)).ShouldBe(
                await TruthIdsAsync(s.OrgId, $"SELECT DISTINCT entry_id FROM journal_lines WHERE org_id = @org AND tenant_id = '{tenant}'", ct),
                ignoreOrder: true);
            (await IdsAsync(conn, tx, "SELECT id FROM accounts", ct)).ShouldBe(
                await TruthIdsAsync(s.OrgId, $"SELECT DISTINCT account_id FROM journal_lines WHERE org_id = @org AND tenant_id = '{tenant}'", ct),
                ignoreOrder: true);
            (await IdsAsync(conn, tx, "SELECT id FROM lease_lite", ct)).ShouldBe([lease]);
            (await IdsAsync(conn, tx, "SELECT id FROM units", ct)).ShouldBe([unit]);
            (await IdsAsync(conn, tx, "SELECT id FROM properties", ct)).ShouldBe([property]);
            (await IdsAsync(conn, tx, "SELECT property_id FROM property_ownership_transfers", ct)).ShouldBe([property]);
            (await IdsAsync(conn, tx, "SELECT id FROM bank_accounts", ct)).ShouldBe([s.Bank]);
            (await CountAsync(conn, tx, "payment_fixtures", ct)).ShouldBe(1);
            (await IdsAsync(conn, tx, "SELECT id FROM payment_operations", ct)).ShouldBe([operation]);
            (await IdsAsync(conn, tx, "SELECT DISTINCT tenant_id FROM payment_operations", ct)).ShouldBe([tenant]);
            (await CountAsync(conn, tx, "payment_observations", ct)).ShouldBe(1);
            (await CountAsync(conn, tx, "org_settings", ct)).ShouldBe(1);
        }
    }

    [Fact]
    public async Task Portal_personas_read_nothing_outside_their_grants()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SeedAsync(ct);
        var tables = await OrgScopedTablesAsync(ct);
        var nonVacuous = new List<string>();

        foreach (var (persona, user, readable) in new[]
        {
            ("owner", s.OwnerAUser, OwnerReadable),
            ("tenant", s.TenantAUser, TenantReadable),
        })
        {
            await using var conn = await fixture.OpenAppConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await RlsProbe.SetOrgContextAsync(conn, tx, s.OrgId, persona, user, ct);

            foreach (var table in tables.Where(t => !readable.Contains(t)))
            {
                (await CountAsync(conn, tx, table, ct)).ShouldBe(0, $"{persona} must read nothing from {table}");
                if (await TruthCountAsync(s.OrgId, $"SELECT count(*) FROM {table} WHERE org_id = @org", ct) > 0)
                {
                    nonVacuous.Add($"{persona}:{table}");
                }
            }
        }

        // The zero above means something only where the org actually holds rows. The gate's presence on
        // the rest is pinned by SchemaGuardTests; here, prove the fixture exercises a real spread.
        nonVacuous.ShouldContain("owner:audit_events");
        nonVacuous.ShouldContain("owner:tenants");
        nonVacuous.ShouldContain("owner:accounts");
        nonVacuous.ShouldContain("owner:payment_operations");
        nonVacuous.ShouldContain("tenant:owners");
        nonVacuous.ShouldContain("tenant:statement_artifacts");
        nonVacuous.ShouldContain("tenant:owner_access");
        nonVacuous.ShouldContain("tenant:audit_events");
        nonVacuous.Count.ShouldBeGreaterThanOrEqualTo(12);
    }

    [Fact]
    public async Task Portal_writes_outside_the_grants_are_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SeedAsync(ct);
        await using var conn = await fixture.OpenAppConnectionAsync(ct);

        // ── Tenant: its own payment and its own audit row are the only inserts it holds ───────────
        (await AffectedAsync(conn, s, "tenant", s.TenantAUser, InsertOperationSql(s, s.TenantA, s.TenantAUser), ct)).ShouldBe(1);
        (await RejectedAsync(conn, s, "tenant", s.TenantAUser, InsertOperationSql(s, s.TenantB, s.TenantAUser), ct))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "a payment for another tenant");
        (await RejectedAsync(conn, s, "tenant", s.TenantAUser, InsertOperationSql(s, s.TenantA, s.TenantBUser), ct))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "a payment submitted in another user's name");
        (await AffectedAsync(conn, s, "tenant", s.TenantAUser, InsertAuditSql(s.TenantAUser, "payment_operations", Guid.NewGuid()), ct)).ShouldBe(1);
        (await RejectedAsync(conn, s, "tenant", s.TenantAUser, InsertAuditSql(s.TenantBUser, "payment_operations", Guid.NewGuid()), ct))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "an audit row attributed to someone else");
        (await RejectedAsync(conn, s, "tenant", s.TenantAUser,
                $"INSERT INTO org_settings (id, org_id, accounting_basis, money_negative_display, created_at, late_fee_amount, late_fee_grace_days, late_fee_kind, late_fee_rate_bps, rent_due_day) " +
                $"VALUES ('{UuidV7.NewId()}', '{s.OrgId}', 'cash', 'minus', now(), 0, 0, 'flat', 0, 1)", ct))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "a readable table is not a writable one");

        // Reads its own payment, but may neither rewrite nor delete it — nor lock-then-update its property.
        (await RejectedAsync(conn, s, "tenant", s.TenantAUser,
                $"UPDATE payment_operations SET amount = 1 WHERE id = '{s.OperationA}'", ct))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "UPDATE of a granted row");
        (await RejectedAsync(conn, s, "tenant", s.TenantAUser,
                $"UPDATE properties SET address = 'x' WHERE id = '{s.PropertyA}'", ct))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "UPDATE of the property its lease names");
        (await AffectedAsync(conn, s, "tenant", s.TenantAUser, $"DELETE FROM payment_operations WHERE id = '{s.OperationA}'", ct))
            .ShouldBe(0, "DELETE is filtered to nothing, not merely unreachable");
        (await AffectedAsync(conn, s, "tenant", s.TenantAUser, $"DELETE FROM tenants WHERE id = '{s.TenantA}'", ct))
            .ShouldBe(0, "a tenant cannot delete its own tenant row either");
        (await TruthCountAsync(s.OrgId, $"SELECT count(*) FROM payment_operations WHERE org_id = @org AND id = '{s.OperationA}'", ct))
            .ShouldBe(1);

        // FOR SHARE applies UPDATE policies' USING: the lock on its own property must still work (the
        // payment eligibility check takes it), and must find nothing on someone else's.
        (await AffectedAsync(conn, s, "tenant", s.TenantAUser, $"SELECT id FROM properties WHERE id = '{s.PropertyA}' FOR SHARE", ct, query: true))
            .ShouldBe(1);
        (await AffectedAsync(conn, s, "tenant", s.TenantAUser, $"SELECT id FROM properties WHERE id = '{s.PropertyB}' FOR SHARE", ct, query: true))
            .ShouldBe(0);

        // The audit log is write-only to a portal persona: not even its own rows read back.
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await RlsProbe.SetOrgContextAsync(conn, tx, s.OrgId, "tenant", s.TenantAUser, ct);
            await ExecAsync(conn, tx, InsertAuditSql(s.TenantAUser, "account-security", s.TenantAUser), ct);
            (await CountAsync(conn, tx, "audit_events", ct)).ShouldBe(0);
            await tx.RollbackAsync(ct);
        }

        // ── Owner: read-only apart from recording its own account-security events ────────────────
        (await AffectedAsync(conn, s, "owner", s.OwnerAUser, InsertAuditSql(s.OwnerAUser, "account-security", s.OwnerAUser), ct)).ShouldBe(1);
        (await RejectedAsync(conn, s, "owner", s.OwnerAUser, InsertAuditSql(s.OwnerAUser, "owners", s.OwnerA), ct))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "an owner records account-security events about itself only");
        (await RejectedAsync(conn, s, "owner", s.OwnerAUser, InsertAuditSql(s.OwnerAUser, "account-security", s.OwnerBUser), ct))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await RejectedAsync(conn, s, "owner", s.OwnerAUser,
                $"INSERT INTO statement_artifacts (id, org_id, owner_id, period_year, period_month, artifact_key, created_at) " +
                $"VALUES ('{UuidV7.NewId()}', '{s.OrgId}', '{s.OwnerA}', 2026, 1, 'forged', now())", ct))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await RejectedAsync(conn, s, "owner", s.OwnerAUser, InsertOperationSql(s, s.TenantA, s.TenantAUser), ct))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "the tenant's insert grant is not the owner's");
        (await RejectedAsync(conn, s, "owner", s.OwnerAUser, $"UPDATE owners SET name = 'x' WHERE id = '{s.OwnerA}'", ct))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await AffectedAsync(conn, s, "owner", s.OwnerAUser, $"DELETE FROM owners WHERE id = '{s.OwnerA}'", ct))
            .ShouldBe(0);
        (await AffectedAsync(conn, s, "owner", s.OwnerAUser, $"DELETE FROM owner_access WHERE user_id = '{s.OwnerAUser}'", ct))
            .ShouldBe(0, "not even its own link row");
    }

    [Fact]
    public async Task A_revoked_link_reads_nothing_from_the_next_statement()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SeedAsync(ct);

        foreach (var (persona, user, linkTable, grantTables) in new[]
        {
            ("owner", s.OwnerA2User, "owner_access", new[] { "owners", "journal_lines", "journal_entries", "properties", "statement_artifacts" }),
            ("tenant", s.TenantA2User, "resident_access", new[] { "tenants", "journal_lines", "journal_entries", "lease_lite", "units", "properties", "payment_operations" }),
        })
        {
            await using var portal = await fixture.OpenAppConnectionAsync(ct);
            await using var tx = await portal.BeginTransactionAsync(ct);
            await RlsProbe.SetOrgContextAsync(portal, tx, s.OrgId, persona, user, ct);
            foreach (var table in grantTables)
            {
                (await CountAsync(portal, tx, table, ct)).ShouldBeGreaterThan(0, $"{persona} reads {table} while linked");
            }

            // Revoked and committed elsewhere while the portal transaction is still open.
            await using (var staff = await fixture.OpenAppConnectionAsync(ct))
            await using (var revoke = await staff.BeginTransactionAsync(ct))
            {
                await RlsProbe.SetOrgContextAsync(staff, revoke, s.OrgId, "system", null, ct);
                (await ExecAsync(staff, revoke, $"UPDATE {linkTable} SET revoked_at = now() WHERE user_id = '{user}' AND revoked_at IS NULL", ct))
                    .ShouldBe(1);
                await revoke.CommitAsync(ct);
            }

            foreach (var table in grantTables)
            {
                (await CountAsync(portal, tx, table, ct)).ShouldBe(0, $"{persona} reads {table} after its link was revoked");
            }

            await tx.RollbackAsync(ct);
        }
    }

    [Theory]
    [InlineData(null)]        // never set on this connection
    [InlineData("")]          // what a reverted SET LOCAL leaves behind
    [InlineData("none")]      // a mixed or unrecognised role set
    [InlineData("admin")]     // a value nobody defined
    [InlineData("Staff")]     // case matters
    [InlineData("staff ")]
    public async Task Unset_none_and_unknown_personas_read_and_write_nothing_even_as_staff(string? persona)
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SeedAsync(ct);
        var tables = await OrgScopedTablesAsync(ct);

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            // A staff user's id, and the staff-shaped query: every row the org holds.
            await RlsProbe.SetOrgContextAsync(conn, tx, s.OrgId, persona, s.StaffUser, ct);
            foreach (var table in tables)
            {
                (await CountAsync(conn, tx, table, ct)).ShouldBe(0, $"persona '{persona ?? "<unset>"}' must read nothing from {table}");
            }

            await tx.RollbackAsync(ct);
        }

        (await RejectedAsync(conn, s, persona, s.StaffUser, InsertAuditSql(s.StaffUser, "account-security", s.StaffUser), ct))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_portal_persona_without_a_link_or_a_user_reads_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SeedAsync(ct);

        foreach (var (persona, user) in new (string, Guid?)[]
        {
            ("owner", null), ("tenant", null),
            ("owner", s.StaffUser), ("tenant", s.StaffUser),   // a real user, but no link
            ("owner", s.TenantAUser), ("tenant", s.OwnerAUser), // linked, but as the other persona
        })
        {
            await using var conn = await fixture.OpenAppConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await RlsProbe.SetOrgContextAsync(conn, tx, s.OrgId, persona, user, ct);
            foreach (var table in new[] { "owners", "tenants", "journal_lines", "journal_entries", "properties", "statement_artifacts", "payment_operations" })
            {
                (await CountAsync(conn, tx, table, ct)).ShouldBe(0, $"{persona} as {user?.ToString() ?? "<no user>"} reads {table}");
            }
        }
    }

    [Theory]
    [InlineData("staff")]
    [InlineData("system")]
    public async Task Staff_and_system_read_and_write_the_whole_organization(string persona)
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SeedAsync(ct);
        var tables = await OrgScopedTablesAsync(ct);

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgContextAsync(conn, tx, s.OrgId, persona, persona == "staff" ? s.StaffUser : null, ct);

        foreach (var table in tables.Where(t => t is not "platform_audit_events"))
        {
            (await CountAsync(conn, tx, table, ct)).ShouldBe(
                await TruthCountAsync(s.OrgId, $"SELECT count(*) FROM {table} WHERE org_id = @org", ct),
                $"{persona} reads every {table} row of its organization");
        }

        (await ExecAsync(conn, tx, $"UPDATE units SET label = label || '' WHERE org_id = '{s.OrgId}'", ct))
            .ShouldBe((int)await TruthCountAsync(s.OrgId, "SELECT count(*) FROM units WHERE org_id = @org", ct));
        (await ExecAsync(conn, tx, $"UPDATE payment_operations SET status = 'Failed' WHERE org_id = '{s.OrgId}'", ct)).ShouldBe(2);
        (await ExecAsync(conn, tx, $"DELETE FROM owner_access WHERE org_id = '{s.OrgId}'", ct)).ShouldBe(3);
        (await ExecAsync(conn, tx, InsertAuditSql(s.StaffUser, "account-security", s.OwnerAUser), ct)).ShouldBe(1);
        await tx.RollbackAsync(ct);
    }

    [Fact]
    public async Task The_platform_plane_still_reads_and_writes_the_capability_tables_with_no_persona()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SeedAsync(ct);

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetPlatformAsync(conn, tx, ct);

        (await ExecAsync(conn, tx,
            $"INSERT INTO entitlements (id, org_id, capability, granted, effective_at, actor) " +
            $"VALUES ('{UuidV7.NewId()}', '{s.OrgId}', 'probe.persona', true, now(), 'test')", ct)).ShouldBe(1);
        (await CountAsync(conn, tx, "entitlements", ct, where: $"org_id = '{s.OrgId}'")).ShouldBe(1);
        await tx.RollbackAsync(ct);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Seeded(
        Guid OrgId,
        Guid OwnerA, Guid OwnerB, Guid PropertyA, Guid PropertyB, Guid PropertyTransferred,
        Guid UnitA, Guid UnitB, Guid TenantA, Guid TenantB, Guid LeaseA, Guid LeaseB, Guid Bank,
        Guid Generation, Guid OperationA, Guid OperationB,
        Guid OwnerAUser, Guid OwnerA2User, Guid OwnerBUser, Guid TenantAUser, Guid TenantA2User, Guid TenantBUser,
        Guid StaffUser);

    private async Task<Seeded> SeedAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "Persona pack", ct);

        async Task<Guid> User(string role, string tag)
        {
            var email = $"{tag}-{orgId:N}@persona.test";
            await AuthTestSupport.CreateUserAsync(fixture, orgId, email, tag, role, ct);
            await using var db = fixture.CreateContext(fixture.MigratorConnectionString);
            return await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(ct);
        }

        var ownerAUser = await User(Roles.Owner, "owner-a");
        var ownerA2User = await User(Roles.Owner, "owner-a2");
        var ownerBUser = await User(Roles.Owner, "owner-b");
        var tenantAUser = await User(Roles.Tenant, "tenant-a");
        var tenantA2User = await User(Roles.Tenant, "tenant-a2");
        var tenantBUser = await User(Roles.Tenant, "tenant-b");
        var staffUser = await User(Roles.PMStaff, "staff");

        var ownerA = new Owner { Id = UuidV7.NewId(), Name = "Persona owner A" };
        var ownerB = new Owner { Id = UuidV7.NewId(), Name = "Persona owner B" };
        var propertyA = new Property { Id = UuidV7.NewId(), OwnerId = ownerA.Id, Address = "1 Persona Way" };
        var propertyB = new Property { Id = UuidV7.NewId(), OwnerId = ownerB.Id, Address = "2 Persona Way" };
        var transferred = new Property { Id = UuidV7.NewId(), OwnerId = ownerA.Id, Address = "3 Persona Way" };
        var unitA = new Unit { Id = UuidV7.NewId(), PropertyId = propertyA.Id, Label = "A1", Rent = new Money(1000m) };
        var unitB = new Unit { Id = UuidV7.NewId(), PropertyId = propertyB.Id, Label = "B1", Rent = new Money(900m) };
        var tenantA = new Tenant { Id = UuidV7.NewId(), DisplayName = "Persona tenant A" };
        var tenantB = new Tenant { Id = UuidV7.NewId(), DisplayName = "Persona tenant B" };
        var leaseA = new LeaseLite
        {
            Id = UuidV7.NewId(),
            TenantId = tenantA.Id,
            UnitId = unitA.Id,
            StartDate = new DateOnly(2026, 1, 1),
            Rent = new Money(1000m),
            DepositRequired = new Money(0m),
            Status = LeaseStatus.Active,
        };
        var leaseB = new LeaseLite
        {
            Id = UuidV7.NewId(),
            TenantId = tenantB.Id,
            UnitId = unitB.Id,
            StartDate = new DateOnly(2026, 1, 1),
            Rent = new Money(900m),
            DepositRequired = new Money(0m),
            Status = LeaseStatus.Active,
        };
        var bank = new BankAccount { Id = UuidV7.NewId(), Name = "Persona trust", Purpose = DirectoryBankPurpose.Trust };
        var date = new DateOnly(2026, 9, 1);

        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(orgId, "test:persona", async () =>
            {
                var db = sp.GetRequiredService<AppDbContext>();
                db.AddRange(ownerA, ownerB, propertyA, propertyB, transferred, unitA, unitB, tenantA, tenantB, leaseA, leaseB, bank);
                await db.SaveChangesAsync(ct);
                await sp.GetRequiredService<IChartOfAccounts>().ProvisionAsync(
                    [new BankAccountSpec(bank.Id, bank.Name, AccountingBankPurpose.Trust)], ct);
                _ = await sp.GetRequiredService<ISender>().Query(new GetOrgSettings(), ct);

                var events = sp.GetRequiredService<IAccountingEvents>();
                await events.PostAsync(new RentCharged(tenantA.Id, propertyA.Id, ownerA.Id, unitA.Id,
                    new Money(1000m), date, "persona rent A", $"persona-rent-a-{orgId:N}"), ct);
                await events.PostAsync(new RentCharged(tenantB.Id, propertyB.Id, ownerB.Id, unitB.Id,
                    new Money(900m), date, "persona rent B", $"persona-rent-b-{orgId:N}"), ct);
                await events.PostAsync(new OwnerContribution(ownerA.Id, transferred.Id, new Money(250m),
                    date, bank.Id, "persona contribution A", $"persona-contribution-a-{orgId:N}"), ct);
                await events.PostAsync(new OwnerContribution(ownerB.Id, propertyB.Id, new Money(300m),
                    date, bank.Id, "persona contribution B", $"persona-contribution-b-{orgId:N}"), ct);

                var owners = sp.GetRequiredService<OwnerAccessService>();
                await owners.GrantAsync(ownerAUser, ownerA.Id, ct);
                await owners.GrantAsync(ownerA2User, ownerA.Id, ct);
                await owners.GrantAsync(ownerBUser, ownerB.Id, ct);
                var residents = sp.GetRequiredService<ResidentAccessService>();
                await residents.GrantAsync(tenantAUser, tenantA.Id, ct);
                await residents.GrantAsync(tenantA2User, tenantA.Id, ct);
                await residents.GrantAsync(tenantBUser, tenantB.Id, ct);
            }, ct);
        }

        // The rows no module command writes on its own, planted raw as the system persona.
        var generation = UuidV7.NewId();
        var operationA = UuidV7.NewId();
        var operationB = UuidV7.NewId();
        await using (var conn = await fixture.OpenAppConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await RlsProbe.SetOrgContextAsync(conn, tx, orgId, "system", null, ct);
            // The transferred property now belongs to Owner B; Owner A's contribution on it stays history.
            await ExecAsync(conn, tx, $"UPDATE properties SET owner_id = '{ownerB.Id}' WHERE id = '{transferred.Id}'", ct);
            foreach (var (property, from, to) in new[] { (transferred.Id, ownerA.Id, ownerB.Id), (propertyA.Id, ownerB.Id, ownerA.Id), (propertyB.Id, ownerA.Id, ownerB.Id) })
            {
                await ExecAsync(conn, tx,
                    "INSERT INTO property_ownership_transfers (id, org_id, property_id, from_owner_id, to_owner_id, effective_date, source_ref, created_at) " +
                    $"VALUES ('{UuidV7.NewId()}', '{orgId}', '{property}', '{from}', '{to}', '2020-01-01', 'persona-{UuidV7.NewId():N}', now())", ct);
            }

            foreach (var owner in new[] { ownerA.Id, ownerB.Id })
            {
                await ExecAsync(conn, tx,
                    "INSERT INTO statement_artifacts (id, org_id, owner_id, period_year, period_month, basis, artifact_key, created_at) " +
                    $"VALUES ('{UuidV7.NewId()}', '{orgId}', '{owner}', 2026, 8, 'cash', 'persona/{owner:N}', now())", ct);
            }

            await ExecAsync(conn, tx,
                $"INSERT INTO payment_fixtures (id, org_id, bank_id, account, created_at) VALUES ('{generation}', '{orgId}', '{bank.Id}', 'acct_persona', now())", ct);
            foreach (var (operation, tenant, user) in new[] { (operationA, tenantA.Id, tenantAUser), (operationB, tenantB.Id, tenantBUser) })
            {
                await ExecAsync(conn, tx,
                    "INSERT INTO payment_operations (id, org_id, tenant_id, user_id, key, generation, bank_id, account, amount, currency, fingerprint, created_at, status, provider_id, processed_count, attempts, due_at) " +
                    $"VALUES ('{operation}', '{orgId}', '{tenant}', '{user}', '{UuidV7.NewId()}', '{generation}', '{bank.Id}', 'acct_persona', 10, 'USD', 'fp', now(), 'Processing', 'prov_{operation:N}', 0, 0, now())", ct);
                await ExecAsync(conn, tx,
                    "INSERT INTO payment_observations (id, org_id, event_id, provider_id, generation, account, mode, kind, gross, fee, net, currency, bank_id, bank_date, evidence_id, payout_id, complete, observed_at, created_at, fingerprint) " +
                    $"VALUES ('{UuidV7.NewId()}', '{orgId}', 'evt_{operation:N}', 'prov_{operation:N}', '{generation}', 'acct_persona', 'Simulation', 'Processing', 10, 0, 10, 'USD', '{bank.Id}', '2026-09-01', '', '', false, now(), now(), 'fp')", ct);
            }

            await tx.CommitAsync(ct);
        }

        return new Seeded(orgId, ownerA.Id, ownerB.Id, propertyA.Id, propertyB.Id, transferred.Id,
            unitA.Id, unitB.Id, tenantA.Id, tenantB.Id, leaseA.Id, leaseB.Id, bank.Id, generation, operationA, operationB,
            ownerAUser, ownerA2User, ownerBUser, tenantAUser, tenantA2User, tenantBUser, staffUser);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static string InsertOperationSql(Seeded s, Guid tenant, Guid user) =>
        "INSERT INTO payment_operations (id, org_id, tenant_id, user_id, key, generation, bank_id, account, amount, currency, fingerprint, created_at, status, processed_count, attempts, due_at) " +
        $"VALUES ('{UuidV7.NewId()}', '{s.OrgId}', '{tenant}', '{user}', '{UuidV7.NewId()}', '{s.Generation}', '{s.Bank}', 'acct_persona', 5, 'USD', 'fp', now(), 'Requested', 0, 0, now())";

    private static string InsertAuditSql(Guid actor, string entityType, Guid entityId) =>
        "INSERT INTO audit_events (id, org_id, actor_user_id, actor_kind, entity_type, entity_id, action, occurred_at) " +
        $"VALUES ('{UuidV7.NewId()}', current_setting('app.org_id')::uuid, '{actor}', 'user', '{entityType}', '{entityId}', 'probe', now())";

    /// <summary>Runs one statement under the persona, in its own rolled-back transaction, and returns
    /// the affected (or, for a query, returned) row count.</summary>
    private static async Task<int> AffectedAsync(
        NpgsqlConnection conn, Seeded s, string? persona, Guid? user, string sql, CancellationToken ct, bool query = false)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgContextAsync(conn, tx, s.OrgId, persona, user, ct);
        int result;
        if (query)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            result = 0;
            while (await reader.ReadAsync(ct)) { result++; }
        }
        else
        {
            result = await ExecAsync(conn, tx, sql, ct);
        }

        await tx.RollbackAsync(ct);
        return result;
    }

    /// <summary>Runs one statement under the persona in its own transaction and returns the SQLSTATE
    /// it raised. Its own transaction because a rejected statement aborts the one it ran in.</summary>
    private static async Task<string> RejectedAsync(
        NpgsqlConnection conn, Seeded s, string? persona, Guid? user, string sql, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgContextAsync(conn, tx, s.OrgId, persona, user, ct);
        var ex = await Should.ThrowAsync<PostgresException>(async () => await ExecAsync(conn, tx, sql, ct));
        await tx.RollbackAsync(ct);
        return ex.SqlState;
    }

    private static async Task<int> ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string table, CancellationToken ct, string? where = null)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM {table}" + (where is null ? "" : $" WHERE {where}"), conn, tx);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<List<Guid>> IdsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0)) { ids.Add(reader.GetGuid(0)); }
        }

        return ids;
    }

    /// <summary>
    /// The container's superuser: the one role row-level security never applies to, so its reads are
    /// the ground truth the filtered reads above are compared with. Test-only by construction — the
    /// fixture's container credentials never exist outside it.
    /// </summary>
    private string SuperuserConnectionString =>
        new NpgsqlConnectionStringBuilder(fixture.MigratorConnectionString)
        {
            Username = "postgres",
            Password = "dev_postgres_pw",
        }.ConnectionString;

    private async Task<long> TruthCountAsync(Guid orgId, string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(SuperuserConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<List<Guid>> TruthIdsAsync(Guid orgId, string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(SuperuserConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(ct)) { ids.Add(reader.GetGuid(0)); }
        return ids;
    }

    private async Task<List<string>> OrgScopedTablesAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(SuperuserConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'org_id' AND NOT a.attisdropped " +
            "WHERE n.nspname = 'public' AND c.relkind = 'r' AND c.relforcerowsecurity ORDER BY 1", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var tables = new List<string>();
        while (await reader.ReadAsync(ct)) { tables.Add(reader.GetString(0)); }
        tables.Count.ShouldBeGreaterThanOrEqualTo(37);
        return tables;
    }
}
