using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.Modules.Accounting.Provisioning;
using LeaseBook.SharedKernel;
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
/// Since M2 (ADR-008) the journal's dimension columns FK to the directory tables, and since M4 (ADR-013)
/// those FKs are <b>composite</b> <c>(org_id, &lt;dim&gt;_id) → (org_id, id)</c>, so any synthetic entry the
/// engine posts needs a directory row <b>in the same org</b> for each owner/tenant/property/unit/bank id it
/// carries. Those rows are <b>FK targets only</b> — never queried by the tests — so the harness materialises
/// them per scope via the migrator with <c>ON CONFLICT DO NOTHING</c>, under that org's RLS context. Bank
/// ids are generated <b>per org</b> (a global-unique <c>bank_accounts.id</c> cannot be shared across orgs)
/// and recorded on the <see cref="OrgScope"/>. <see cref="ProvisionedScopeAsync"/> seeds the three banks
/// plus any dims passed in; <see cref="EnsureDirectoryAsync"/> seeds dims generated after the scope exists.
/// </para>
/// </summary>
internal static class AccountingTestHarness
{
    /// <summary>
    /// A fresh org with the chart of accounts provisioned, the three directory bank rows present, and
    /// minimal directory rows for any dimension ids passed in (so the journal-dimension FKs resolve, P38).
    /// </summary>
    public static async Task<OrgScope> ProvisionedScopeAsync(
        PostgresFixture fixture, CancellationToken ct,
        Guid[]? owners = null, Guid[]? tenants = null, Guid[]? properties = null, Guid[]? units = null)
    {
        var scope = await OrgScope.CreateAsync(fixture, ct);

        // Bank ids are per-org now (ADR-013): the composite (org_id, bank_account_id) FK is org-scoped and
        // bank_accounts.id is globally unique, so a fixed id cannot be seeded into more than one org.
        scope.SetBankIds(UuidV7.NewId(), UuidV7.NewId(), UuidV7.NewId());

        await scope.RunAsync(() => new ChartOfAccounts(scope.Db).ProvisionAsync(
            [
                new BankAccountSpec(scope.TrustBankId, "Operating Trust", BankPurpose.Trust),
                new BankAccountSpec(scope.DepositBankId, "Deposit Trust", BankPurpose.Deposit),
                new BankAccountSpec(scope.OperatingBankId, "Management Operating", BankPurpose.Operating),
            ], ct), ct);

        await EnsureDirectoryAsync(fixture, scope, ct, owners, tenants, properties, units);
        return scope;
    }

    /// <summary>
    /// Materialises the three banks plus directory rows for the given dimension ids as FK targets
    /// (idempotent, RLS-bypassing). Call with ids generated after the scope exists (property suites).
    /// </summary>
    public static async Task EnsureDirectoryAsync(
        PostgresFixture fixture, OrgScope scope, CancellationToken ct,
        Guid[]? owners = null, Guid[]? tenants = null, Guid[]? properties = null, Guid[]? units = null)
    {
        var orgId = scope.OrgId;

        // Per-org sentinels give seeded properties an owner and seeded units a property (intra-directory
        // FKs) without the harness needing a dimension's real parent. Generated per call so they never
        // collide across orgs on the global PK.
        var sentinelOwnerId = UuidV7.NewId();
        var sentinelPropertyId = UuidV7.NewId();

        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);

        // The directory tables FORCE row security even for the migrator (table owner), so the WITH CHECK
        // policy needs an org context — set it transaction-locally to this scope's org. The org row itself
        // already exists (OrgScope.CreateAsync inserted it).
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);

        await UpsertAsync(conn, tx, orgId, ct, "bank_accounts", "name, purpose",
            (scope.TrustBankId, "'Operating Trust', 'trust'"),
            (scope.DepositBankId, "'Deposit Trust', 'deposit'"),
            (scope.OperatingBankId, "'Management Operating', 'operating'"));

        // Sentinels first — properties/units reference them (intra-directory FKs). Only seed them when a
        // parent is actually needed, so an org that seeds no properties/units stays free of placeholder
        // rows (e.g. the isolation test's "empty" second org).
        if (properties is { Length: > 0 } || units is { Length: > 0 })
        {
            await UpsertAsync(conn, tx, orgId, ct, "owners", "name", (sentinelOwnerId, "'Harness sentinel owner'"));
        }

        if (units is { Length: > 0 })
        {
            await UpsertAsync(conn, tx, orgId, ct, "properties", "owner_id, address",
                (sentinelPropertyId, $"'{sentinelOwnerId}', 'Harness sentinel property'"));
        }

        if (owners is { Length: > 0 })
        {
            await UpsertAsync(conn, tx, orgId, ct, "owners", "name",
                [.. owners.Distinct().Select(id => (id, $"'Test owner {id:N}'"))]);
        }

        if (properties is { Length: > 0 })
        {
            await UpsertAsync(conn, tx, orgId, ct, "properties", "owner_id, address",
                [.. properties.Distinct().Select(id => (id, $"'{sentinelOwnerId}', 'Test property {id:N}'"))]);
        }

        if (units is { Length: > 0 })
        {
            await UpsertAsync(conn, tx, orgId, ct, "units", "property_id, label, status",
                [.. units.Distinct().Select(id => (id, $"'{sentinelPropertyId}', 'Test unit {id:N}', 'occupied'"))]);
        }

        if (tenants is { Length: > 0 })
        {
            await UpsertAsync(conn, tx, orgId, ct, "tenants", "display_name, status",
                [.. tenants.Distinct().Select(id => (id, $"'Test tenant {id:N}', 'current'"))]);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>Inserts each row into <paramref name="orgId"/> with the given extra columns, skipping existing PKs.</summary>
    private static async Task UpsertAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid orgId, CancellationToken ct, string table, string columns,
        params (Guid Id, string Values)[] rows)
    {
        foreach (var (id, values) in rows)
        {
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO {table} (id, org_id, {columns}, created_at) " +
                $"VALUES ('{id}', '{orgId}', {values}, now()) ON CONFLICT (id) DO NOTHING", conn, tx);
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
