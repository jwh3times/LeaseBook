using LeaseBook.Tests.Integration.Fixtures;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// CI-permanent guard (§C.3): walks the live schema after migrations and fails if any table with an
/// <c>org_id</c> column lacks FORCE row security + an isolation policy, or if any table <i>without</i>
/// <c>org_id</c> is not in the table-class allowlist. A future migration that adds an org-scoped table
/// but forgets <c>EnableOrgRls</c> fails here even if no other test happens to touch that table.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SchemaGuardTests(PostgresFixture fixture)
{
    /// <summary>
    /// Global-class tables: no <c>org_id</c>, no RLS, each justified (§C.3).
    /// </summary>
    private static readonly HashSet<string> GlobalTables = new(StringComparer.Ordinal)
    {
        "orgs",                  // global-class: the org IS the tenant — it has no org_id
        "__EFMigrationsHistory", // EF migration bookkeeping — not org data
    };

    /// <summary>
    /// Identity-class tables (§C.3 / pitfall E6): exempt from RLS even though <c>asp_net_users</c>
    /// carries an <c>org_id</c> — authentication must work before any org context exists, so user
    /// isolation is enforced by app logic, not by a row-security policy.
    /// </summary>
    private static readonly HashSet<string> IdentityTables = new(StringComparer.Ordinal)
    {
        "asp_net_users", "asp_net_roles", "asp_net_user_claims", "asp_net_user_roles",
        "asp_net_user_logins", "asp_net_role_claims", "asp_net_user_tokens",
    };

    [Fact]
    public async Task Every_org_scoped_table_is_force_rls_with_a_policy_and_every_other_table_is_allowlisted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);

        var tables = await ReadTablesAsync(conn, ct);
        var orgScoped = await ReadNamesAsync(conn,
            "SELECT table_name FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND column_name = 'org_id'", ct);
        var policied = await ReadNamesAsync(conn,
            "SELECT tablename FROM pg_policies WHERE schemaname = 'public'", ct);

        var failures = new List<string>();
        foreach (var (name, rowSecurity, forceRowSecurity) in tables)
        {
            if (IdentityTables.Contains(name))
            {
                continue; // identity-class — protected by app logic, not RLS
            }

            if (orgScoped.Contains(name))
            {
                if (!rowSecurity || !forceRowSecurity)
                {
                    failures.Add($"{name}: org-scoped but RLS not ENABLEd+FORCEd " +
                                 $"(relrowsecurity={rowSecurity}, relforcerowsecurity={forceRowSecurity}).");
                }

                if (!policied.Contains(name))
                {
                    failures.Add($"{name}: org-scoped but has no row-level security policy.");
                }
            }
            else if (!GlobalTables.Contains(name))
            {
                failures.Add($"{name}: has no org_id and is not in the §C.3 table-class allowlist.");
            }
        }

        failures.ShouldBeEmpty(failures.Count == 0 ? "" : string.Join(Environment.NewLine, failures));

        // Sanity: the guard is actually looking at our schema, not an empty catalog.
        orgScoped.ShouldContain("audit_events");
    }

    private static async Task<List<(string Name, bool RowSecurity, bool ForceRowSecurity)>> ReadTablesAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity " +
            "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = 'public' AND c.relkind = 'r'", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = new List<(string, bool, bool)>();
        while (await reader.ReadAsync(ct))
        {
            result.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        }

        return result;
    }

    private static async Task<HashSet<string>> ReadNamesAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
