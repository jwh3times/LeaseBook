using LeaseBook.Tests.Common;
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
    /// Global-class tables: no <c>org_id</c>, each justified (§C.3). Most carry no RLS either;
    /// <c>feature_flags</c> is the exception and is additionally asserted via
    /// <see cref="PlatformOnlyTables"/>.
    /// </summary>
    private static readonly HashSet<string> GlobalTables = new(StringComparer.Ordinal)
    {
        "orgs",                  // global-class: the org IS the tenant — it has no org_id
        "__EFMigrationsHistory", // EF migration bookkeeping — not org data
        "feature_flags",         // global-class (ADR-028): a flag is a property of the deployment,
                                 // not of a tenant, so it has no org_id and lands in this arm. It is
                                 // the one entry here that still carries RLS — its writes are gated on
                                 // platform scope, so a tenant-plane path cannot toggle a flag, and
                                 // that is asserted by PlatformWrittenTables below. The other three
                                 // capability tables DO carry org_id and get real RLS with a platform
                                 // escape — they pass the org-scoped arm above and need no entry here.
    };

    /// <summary>
    /// Platform-written tables (ADR-028): every policy that can admit a row must be gated on
    /// <c>app.platform = 'on'</c>. Asserted POSITIVELY rather than exempted, and asserted on the
    /// policy <i>predicates</i> rather than on mere policy existence — the org-scoped arm below never
    /// inspects <c>feature_flags</c> (no <c>org_id</c>), and the <see cref="GlobalTables"/> arm checks
    /// membership only, so without this a migration could <c>DISABLE ROW LEVEL SECURITY</c> on it and
    /// leave the suite green while any tenant toggled a flag.
    /// <para>
    /// The "every non-null WITH CHECK is a platform gate and nothing else" clause is the one that
    /// catches the write-permissive regression on all four tables at once: a WITH CHECK that ORs org
    /// equality in alongside the GUC lets a tenant insert its own <c>granted = true</c> entitlement
    /// row and self-grant a paid capability. See <see cref="GatedOnPlatform"/> — mentioning the GUC
    /// is necessary but not sufficient, which is precisely how that shape sneaks through a naive
    /// substring check.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> PlatformWrittenTables = new(StringComparer.Ordinal)
    {
        "feature_flags",         // a tenant-plane path must not be able to toggle a deployment flag
        "platform_audit_events", // the platform audit trail is written only by the platform plane
        "entitlements",          // a tenant must not be able to grant itself a paid capability
        "capability_cohorts",    // nor add itself to a rollout cohort
    };

    /// <summary>
    /// The subset whose READS are platform-only too: no policy on these may admit a row without
    /// platform scope. <c>feature_flags</c> is deliberately NOT here — it is tenant-readable so the
    /// capability resolver can read a kill switch inside the ambient request transaction (ADR-028) —
    /// which is exactly why the read gate needs asserting on the table that still has one.
    /// </summary>
    private static readonly HashSet<string> PlatformOnlyReadTables = new(StringComparer.Ordinal)
    {
        "platform_audit_events", // who granted what to whom is never visible inside a tenant session
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
        var policies = await ReadPoliciesAsync(conn, ct);
        var policied = policies.Select(p => p.Table).ToHashSet(StringComparer.Ordinal);

        var failures = new List<string>();
        var seenPlatformWritten = new HashSet<string>(StringComparer.Ordinal);
        var seenPlatformOnlyRead = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, rowSecurity, forceRowSecurity) in tables)
        {
            if (IdentityTables.Contains(name))
            {
                continue; // identity-class — protected by app logic, not RLS
            }

            // Positive assertion, deliberately outside the org_id branch below: feature_flags has no
            // org_id, so neither the org-scoped arm nor the allowlist arm would ever inspect its RLS.
            if (PlatformWrittenTables.Contains(name))
            {
                seenPlatformWritten.Add(name);
                var own = policies.Where(p => p.Table == name).ToList();

                if (!rowSecurity || !forceRowSecurity)
                {
                    failures.Add($"{name}: platform-written but RLS not ENABLEd+FORCEd " +
                                 $"(relrowsecurity={rowSecurity}, relforcerowsecurity={forceRowSecurity}).");
                }

                if (own.Count == 0)
                {
                    failures.Add($"{name}: platform-written but has no row-level security policy.");
                }

                if (!own.Any(p => GatedOnPlatform(p.WithCheck)))
                {
                    failures.Add($"{name}: platform-written but no policy admits rows on app.platform — " +
                                 "nothing can write it, or the gate was dropped.");
                }

                foreach (var open in own.Where(p => p.WithCheck is not null && !GatedOnPlatform(p.WithCheck)))
                {
                    failures.Add($"{name}: policy {open.Name} admits writes without platform scope " +
                                 $"(WITH CHECK {open.WithCheck}) — a tenant could write this table.");
                }
            }

            if (PlatformOnlyReadTables.Contains(name))
            {
                seenPlatformOnlyRead.Add(name);

                foreach (var open in policies.Where(p =>
                             p.Table == name && p.Qual is not null && !GatedOnPlatform(p.Qual)))
                {
                    failures.Add($"{name}: policy {open.Name} admits reads without platform scope " +
                                 $"(USING {open.Qual}) — this table is never visible in a tenant session.");
                }
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

        // A renamed or dropped platform table must not silently take its assertion with it.
        seenPlatformWritten.ShouldBe(PlatformWrittenTables, ignoreOrder: true);
        seenPlatformOnlyRead.ShouldBe(PlatformOnlyReadTables, ignoreOrder: true);

        failures.ShouldBeEmpty(failures.Count == 0 ? "" : string.Join(Environment.NewLine, failures));

        // Sanity: the guard is actually looking at our schema, not an empty catalog.
        orgScoped.ShouldContain("audit_events");
    }

    /// <summary>
    /// A predicate is a platform gate only if it turns on <c>app.platform</c> and on nothing else.
    /// The <c>org_id</c> clause is what makes this more than a substring check: the write-permissive
    /// shape this guard exists to catch is
    /// <c>WITH CHECK (org_id = my_org OR current_setting('app.platform', ...) = 'on')</c>, which
    /// mentions the GUC and is still a self-grant hole — a tenant satisfies the left branch for its
    /// own rows. Mentioning the GUC is necessary but nowhere near sufficient.
    /// <para>
    /// Matching is on the GUC name rather than the whole expression because Postgres normalizes what
    /// it stores: the emitted <c>current_setting('app.platform', true) = 'on'</c> comes back as
    /// <c>(current_setting('app.platform'::text, true) = 'on'::text)</c>.
    /// </para>
    /// </summary>
    private static bool GatedOnPlatform(string? predicate) =>
        predicate?.Contains("app.platform", StringComparison.Ordinal) == true &&
        predicate?.Contains("org_id", StringComparison.Ordinal) == false;

    private static async Task<List<(string Table, string Name, string? Qual, string? WithCheck)>> ReadPoliciesAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT tablename, policyname, qual, with_check FROM pg_policies WHERE schemaname = 'public'", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = new List<(string, string, string?, string?)>();
        while (await reader.ReadAsync(ct))
        {
            result.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return result;
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
