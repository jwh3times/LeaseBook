using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.Modules.Accounting.Provisioning;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LeaseBook.Tests.Accounting.Support;

/// <summary>
/// Shared setup for engine tests: a fresh org with a provisioned chart of accounts (one trust, one
/// deposit, one operating bank) and factories for the module-internal services bound to that scope's
/// context. Construction goes through the real services — never raw SQL — so the engine is always the
/// thing under test (§A money-path discipline).
/// <para>
/// Since M2 (ADR-008) the journal's dimension columns FK to the directory tables, so any synthetic
/// entry the engine posts needs a directory row for each owner/tenant/property/unit/bank id it carries.
/// Those rows are <b>FK targets only</b> — never queried by the tests — so the harness materialises them
/// once in a hidden "harness directory" org via the migrator with <c>ON CONFLICT DO NOTHING</c>. Postgres
/// FK checks bypass row security, so a single global row per id satisfies every test org's FK even though
/// RLS hides it from them. This keeps the fixed dimension ids the suites reuse across orgs collision-free
/// (the PK is global) without per-org seeding. <see cref="ProvisionedScopeAsync"/> seeds the three banks
/// plus any dims passed in; <see cref="EnsureDirectoryAsync"/> seeds dims generated after the scope exists.
/// </para>
/// </summary>
internal static class AccountingTestHarness
{
    public static readonly Guid TrustBankId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    public static readonly Guid DepositBankId = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    public static readonly Guid OperatingBankId = Guid.Parse("00000000-0000-0000-0000-0000000000b3");

    // The hidden org the FK-target directory rows hang off (invisible to test orgs via RLS; FK checks
    // bypass RLS). Sentinels give seeded properties an owner and seeded units a property (intra-directory
    // FKs) without the harness needing to know a dimension's real parent.
    private static readonly Guid HarnessDirectoryOrgId = Guid.Parse("00000000-0000-0000-0000-0000000d1100");
    private static readonly Guid SentinelOwnerId = Guid.Parse("00000000-0000-0000-0000-0000000000fe");
    private static readonly Guid SentinelPropertyId = Guid.Parse("00000000-0000-0000-0000-0000000000fd");

    /// <summary>
    /// A fresh org with the chart of accounts provisioned, the three directory bank rows present, and
    /// minimal directory rows for any dimension ids passed in (so the journal-dimension FKs resolve, P38).
    /// </summary>
    public static async Task<OrgScope> ProvisionedScopeAsync(
        PostgresFixture fixture, CancellationToken ct,
        Guid[]? owners = null, Guid[]? tenants = null, Guid[]? properties = null, Guid[]? units = null)
    {
        var scope = await OrgScope.CreateAsync(fixture, ct);
        await scope.RunAsync(() => new ChartOfAccounts(scope.Db).ProvisionAsync(
            [
                new BankAccountSpec(TrustBankId, "Operating Trust", BankPurpose.Trust),
                new BankAccountSpec(DepositBankId, "Deposit Trust", BankPurpose.Deposit),
                new BankAccountSpec(OperatingBankId, "Management Operating", BankPurpose.Operating),
            ], ct), ct);

        await EnsureDirectoryAsync(fixture, ct, owners, tenants, properties, units);
        return scope;
    }

    /// <summary>
    /// Materialises the three banks plus directory rows for the given dimension ids as FK targets
    /// (idempotent, RLS-bypassing). Call with ids generated after the scope exists (property suites).
    /// </summary>
    public static async Task EnsureDirectoryAsync(
        PostgresFixture fixture, CancellationToken ct,
        Guid[]? owners = null, Guid[]? tenants = null, Guid[]? properties = null, Guid[]? units = null)
    {
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);

        // orgs is global-class (no RLS) — the harness org these FK-target rows hang off.
        await using (var org = new NpgsqlCommand(
            "INSERT INTO orgs (id, name, created_at) VALUES (@org, 'Harness Directory', now()) ON CONFLICT (id) DO NOTHING", conn))
        {
            org.Parameters.AddWithValue("org", HarnessDirectoryOrgId);
            await org.ExecuteNonQueryAsync(ct);
        }

        // The directory tables FORCE row security even for the migrator (table owner), so the WITH CHECK
        // policy needs an org context — set it transaction-locally to the harness org.
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, HarnessDirectoryOrgId, ct);

        await UpsertAsync(conn, tx, ct, "bank_accounts", "name, purpose",
            (TrustBankId, "'Operating Trust', 'trust'"),
            (DepositBankId, "'Deposit Trust', 'deposit'"),
            (OperatingBankId, "'Management Operating', 'operating'"));

        // Sentinels first — properties/units reference them (intra-directory FKs).
        await UpsertAsync(conn, tx, ct, "owners", "name", (SentinelOwnerId, "'Harness sentinel owner'"));
        await UpsertAsync(conn, tx, ct, "properties", "owner_id, address",
            (SentinelPropertyId, $"'{SentinelOwnerId}', 'Harness sentinel property'"));

        if (owners is { Length: > 0 })
        {
            await UpsertAsync(conn, tx, ct, "owners", "name",
                [.. owners.Distinct().Select(id => (id, $"'Test owner {id:N}'"))]);
        }

        if (properties is { Length: > 0 })
        {
            await UpsertAsync(conn, tx, ct, "properties", "owner_id, address",
                [.. properties.Distinct().Select(id => (id, $"'{SentinelOwnerId}', 'Test property {id:N}'"))]);
        }

        if (units is { Length: > 0 })
        {
            await UpsertAsync(conn, tx, ct, "units", "property_id, label, status",
                [.. units.Distinct().Select(id => (id, $"'{SentinelPropertyId}', 'Test unit {id:N}', 'occupied'"))]);
        }

        if (tenants is { Length: > 0 })
        {
            await UpsertAsync(conn, tx, ct, "tenants", "display_name, status",
                [.. tenants.Distinct().Select(id => (id, $"'Test tenant {id:N}', 'current'"))]);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>Inserts each row into the harness org with the given extra columns, skipping existing PKs.</summary>
    private static async Task UpsertAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, string table, string columns,
        params (Guid Id, string Values)[] rows)
    {
        foreach (var (id, values) in rows)
        {
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO {table} (id, org_id, {columns}, created_at) " +
                $"VALUES ('{id}', '{HarnessDirectoryOrgId}', {values}, now()) ON CONFLICT (id) DO NOTHING", conn, tx);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public static PostingService Posting(OrgScope scope) => new(scope.Db, scope.Tenant, new AccountingPeriods(scope.Db));

    public static ReversalService Reversal(OrgScope scope) => new(scope.Db, scope.Tenant, Posting(scope));

    public static AccountingPeriods Periods(OrgScope scope) => new(scope.Db);

    public static AccountingEventService Events(OrgScope scope) =>
        new(scope.Db, Posting(scope), new PostingLock(scope.Db, scope.Tenant));

    /// <summary>A posted line projected to its resolved account code + amounts/basis/dims, for exact assertions.</summary>
    public sealed record LineView(
        string Code, decimal? Debit, decimal? Credit, EntryBasis Basis,
        Guid? TenantId, Guid? OwnerId, Guid? PropertyId, Guid? UnitId, Guid? BankAccountId);

    public static async Task<List<LineView>> ReadLinesAsync(OrgScope scope, Guid entryId, CancellationToken ct)
    {
        List<LineView> lines = [];
        await scope.RunAsync(async () =>
        {
            var rows = await (
                from line in scope.Db.Set<JournalLine>().AsNoTracking()
                join account in scope.Db.Set<Account>().AsNoTracking() on line.AccountId equals account.Id
                where line.EntryId == entryId
                select new
                {
                    account.Code,
                    line.Debit,
                    line.Credit,
                    line.Basis,
                    line.TenantId,
                    line.OwnerId,
                    line.PropertyId,
                    line.UnitId,
                    line.BankAccountId,
                }).ToListAsync(ct);

            lines = rows
                .Select(r => new LineView(
                    r.Code, r.Debit?.Amount, r.Credit?.Amount, r.Basis,
                    r.TenantId, r.OwnerId, r.PropertyId, r.UnitId, r.BankAccountId))
                .ToList();
        }, ct);

        return lines;
    }
}
