using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

// Testcontainers pulls in BouncyCastle, whose root namespace `Org` shadows the entity type.
using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-01 migration smoke test: proves the four accounting tables exist with their guarantees by
/// writing a balanced entry through the module's internal factories (the only construction path) as
/// the RLS-subject <b>app role</b>, reading it back, and checking the NUMERIC(14,2) Money round-trip is
/// exact. Also demonstrates the append-only grant: the app role cannot UPDATE/DELETE journal rows.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class AccountingSchemaRoundTripTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_balanced_entry_round_trips_through_the_app_role_with_exact_money()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        await CreateOrgAsync(orgId, ct);

        var tenant = new TenantContext();
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        // A genuine two-decimal amount — the point is that scale 2 survives the NUMERIC(14,2) round-trip.
        var amount = new Money(1234.56m);
        var propertyId = UuidV7.NewId();
        var ownerId = UuidV7.NewId();
        var tenantId = UuidV7.NewId();
        Guid entryId = default;

        await executor.RunAsync(orgId, async () =>
        {
            var receivable = Account.Create("tenant_receivable", AccountClass.TenantReceivable, "Tenant Receivable", null);
            var ownerEquity = Account.Create("owner_equity", AccountClass.OwnerEquity, "Owner Equity", null);
            db.Set<Account>().AddRange(receivable, ownerEquity);

            // M2 (ADR-008): the journal-dimension FKs require directory rows for the dims these lines
            // carry — materialize them in this org before posting so the FK targets exist.
            db.Set<Owner>().Add(new Owner { Id = ownerId, Name = "Round-trip owner" });
            db.Set<Property>().Add(new Property { Id = propertyId, OwnerId = ownerId, Address = "Round-trip property" });
            db.Set<Tenant>().Add(new Tenant { Id = tenantId, DisplayName = "Round-trip tenant", Status = TenantStatus.Current });
            await db.SaveChangesAsync(ct);

            var entry = JournalEntry.Create(
                new DateOnly(2026, 2, 1), "RentCharged", null, "Round-trip test", sourceRef: null,
                reversesEntryId: null, createdBy: null, postedAt: DateTime.UtcNow);
            entry.AddLine(JournalLine.Create(
                receivable.Id, AccountClass.TenantReceivable, debit: amount, credit: null, EntryBasis.Accrual,
                propertyId: propertyId, ownerId: ownerId, tenantId: tenantId));
            entry.AddLine(JournalLine.Create(
                ownerEquity.Id, AccountClass.OwnerEquity, debit: null, credit: amount, EntryBasis.Accrual,
                propertyId: propertyId, ownerId: ownerId));
            db.Set<JournalEntry>().Add(entry);
            await db.SaveChangesAsync(ct);
            entryId = entry.Id;
        }, ct);

        JournalEntry? readback = null;
        await executor.RunAsync(orgId, async () =>
        {
            readback = await db.Set<JournalEntry>().AsNoTracking()
                .Include(e => e.Lines)
                .SingleOrDefaultAsync(e => e.Id == entryId, ct);
        }, ct);

        readback.ShouldNotBeNull();
        readback.OrgId.ShouldBe(orgId);                       // stamped by the interceptor
        readback.EntryDate.ShouldBe(new DateOnly(2026, 2, 1));
        readback.EventType.ShouldBe("RentCharged");
        readback.CreatedAt.ShouldNotBe(default);              // stamped on insert
        readback.Lines.Count.ShouldBe(2);

        var debitLine = readback.Lines.Single(l => l.Debit is not null);
        var creditLine = readback.Lines.Single(l => l.Credit is not null);

        debitLine.Debit!.Value.Amount.ShouldBe(1234.56m);    // exact — no silent rounding/scale loss
        debitLine.Credit.ShouldBeNull();
        debitLine.AccountClass.ShouldBe(AccountClass.TenantReceivable);
        debitLine.Basis.ShouldBe(EntryBasis.Accrual);
        debitLine.TenantId.ShouldBe(tenantId);

        creditLine.Credit!.Value.Amount.ShouldBe(1234.56m);
        creditLine.Debit.ShouldBeNull();
        creditLine.AccountClass.ShouldBe(AccountClass.OwnerEquity);
    }

    [Fact]
    public async Task App_role_cannot_update_or_delete_journal_rows()
    {
        var ct = TestContext.Current.CancellationToken;

        // The missing UPDATE/DELETE grant is a table-level privilege, checked before any row or RLS
        // access — so no org context is needed, and each statement runs standalone (a shared
        // transaction would be poisoned by the first failure: 25P02 instead of 42501).
        await using var conn = await fixture.OpenAppConnectionAsync(ct);

        await ShouldBePermissionDeniedAsync(conn, "UPDATE journal_entries SET description = 'tamper'", ct);
        await ShouldBePermissionDeniedAsync(conn, "DELETE FROM journal_entries", ct);
        await ShouldBePermissionDeniedAsync(conn, "UPDATE journal_lines SET memo = 'tamper'", ct);
        await ShouldBePermissionDeniedAsync(conn, "DELETE FROM journal_lines", ct);
    }

    private static async Task ShouldBePermissionDeniedAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        var ex = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        });
        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"expected permission denied for: {sql}");
        ex.MessageText.ShouldContain("permission denied");
    }

    private async Task CreateOrgAsync(Guid orgId, CancellationToken ct)
    {
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = "Tarheel Property Group" });
        await migratorDb.SaveChangesAsync(ct);
    }
}
