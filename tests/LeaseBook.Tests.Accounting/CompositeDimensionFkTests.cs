using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// ADR-013 / P60: the journal-dimension FKs are composite <c>(org_id, &lt;dim&gt;_id) → (org_id, id)</c>, so
/// a journal line in one org cannot reference a directory row that exists only in another org. Before the
/// rework a single-column FK proved only that the id existed in <i>some</i> org (Postgres referential
/// integrity bypasses RLS); the negative test below fails against the old single-column constraint and
/// passes against the composite one. Rows are planted as the migrator (bypassing the engine) so the FK is
/// the thing under test (§A money-path note).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CompositeDimensionFkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_journal_line_cannot_reference_a_bank_from_another_org()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var orgA = await ProvisionedScopeAsync(fixture, ct);
        await using var orgB = await ProvisionedScopeAsync(fixture, ct);

        var trustAccountId = await TrustBankAccountIdAsync(orgA, ct);

        // org A's own trust-bank account, but the bank dimension points at org B's bank → the composite
        // (org_id, bank_account_id) FK has no (orgA, orgB-bank) target and rejects the line.
        var ex = await Should.ThrowAsync<PostgresException>(() =>
            InsertBankLineAsync(orgA.OrgId, trustAccountId, bankAccountId: orgB.TrustBankId, ct));

        ex.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task A_journal_line_can_reference_a_bank_in_its_own_org()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        var trustAccountId = await TrustBankAccountIdAsync(scope, ct);

        // Same-org bank id satisfies the composite FK — no throw.
        await InsertBankLineAsync(scope.OrgId, trustAccountId, bankAccountId: scope.TrustBankId, ct);
    }

    private static async Task<Guid> TrustBankAccountIdAsync(OrgScope scope, CancellationToken ct)
    {
        var code = AccountCodes.TrustBank(scope.TrustBankId);
        Guid id = default;
        await scope.RunAsync(async () => id = await scope.Db.Set<Account>()
            .Where(a => a.Code == code).Select(a => a.Id).SingleAsync(ct), ct);
        return id;
    }

    private async Task InsertBankLineAsync(Guid orgId, Guid accountId, Guid bankAccountId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // FORCE ROW LEVEL SECURITY applies even to the migrator (table owner), so the WITH CHECK policy
        // needs an org context — set it for this transaction.
        await using (var setOrg = new NpgsqlCommand("SELECT set_config('app.org_id', @org, true)", conn, tx))
        {
            setOrg.Parameters.AddWithValue("org", orgId.ToString());
            await setOrg.ExecuteNonQueryAsync(ct);
        }

        var entryId = UuidV7.NewId();
        await using (var entry = new NpgsqlCommand(
            "INSERT INTO journal_entries (id, org_id, entry_date, event_type, posted_at, created_at) " +
            "VALUES (@id, @org, DATE '2026-02-01', 'FkProbe', now(), now())", conn, tx))
        {
            entry.Parameters.AddWithValue("id", entryId);
            entry.Parameters.AddWithValue("org", orgId);
            await entry.ExecuteNonQueryAsync(ct);
        }

        await using (var line = new NpgsqlCommand(
            "INSERT INTO journal_lines (id, org_id, entry_id, account_id, account_class, debit, basis, bank_account_id, created_at) " +
            "VALUES (@id, @org, @entry, @account, 'trust_bank', 100.00, 'both', @bank, now())", conn, tx))
        {
            line.Parameters.AddWithValue("id", UuidV7.NewId());
            line.Parameters.AddWithValue("org", orgId);
            line.Parameters.AddWithValue("entry", entryId);
            line.Parameters.AddWithValue("account", accountId);
            line.Parameters.AddWithValue("bank", bankAccountId);
            await line.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
}
