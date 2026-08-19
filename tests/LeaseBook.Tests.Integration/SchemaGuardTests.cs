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
    /// <see cref="ExpectedPlatformPolicies"/>.
    /// </summary>
    private static readonly HashSet<string> GlobalTables = new(StringComparer.Ordinal)
    {
        "orgs",                  // global-class: the organization catalog — it has no org_id
        "__EFMigrationsHistory", // EF migration bookkeeping — not org data
        "feature_flags",         // global-class (ADR-028): a flag is a property of the deployment,
                                 // not of an organization, so it has no org_id and lands in this arm. It is
                                 // the one entry here that still carries RLS — its writes are gated on
                                 // platform scope, so an organization-plane path cannot toggle a flag, and
                                 // that is asserted by ExpectedPlatformPolicies below. The other three
                                 // capability tables DO carry org_id and get real RLS with a platform
                                 // escape — they pass the org-scoped arm above and need no entry here.
        "data_protection_keys",  // global-class (F8 / ADR-041): the Data Protection keyring belongs to
                                 // the deployment, not to an organization, so it has no org_id and no
                                 // RLS. It stores wrapped key material and its metadata — never
                                 // organization data — and is read/written only by KeyringDbContext.
    };

    // The three predicates the capability migration emits, as Postgres normalizes and stores them:
    // it re-prints the parse tree, so 'app.platform' becomes 'app.platform'::text and so on. These
    // are compared literally, which is the point — see ExpectedPlatformPolicies.
    private const string PlatformGate = "(current_setting('app.platform'::text, true) = 'on'::text)";

    private const string OrgOrPlatform =
        "((org_id = (NULLIF(current_setting('app.org_id'::text, true), ''::text))::uuid) " +
        "OR (current_setting('app.platform'::text, true) = 'on'::text))";

    private const string Unconditional = "true";

    /// <summary>
    /// The exact policy set for every platform table (ADR-028), pinned. Any deviation — an added
    /// policy, a removed one, a changed command, a changed role list, a changed predicate — fails,
    /// and the only way to make it pass is to edit this expectation deliberately.
    /// <para>
    /// This deliberately inverts the default. Two rounds of heuristics both had blind spots that sat
    /// one word away from the hole they existed to catch:
    /// </para>
    /// <list type="bullet">
    /// <item>"some policy's WITH CHECK mentions app.platform" passed the write-permissive
    /// <c>WITH CHECK (org_id = my_org OR platform)</c>, which names the GUC and is still a self-grant
    /// hole — a tenant satisfies the left branch for its own rows.</item>
    /// <item>Adding "…and does not mention org_id" fixed only that literal shape. <c>OR true</c>,
    /// <c>OR current_user = 'leasebook_app'</c> and any helper function are all semantically identical
    /// holes that never contain the string <c>org_id</c>.</item>
    /// <item>Filtering to <c>with_check IS NOT NULL</c> skipped the most dangerous shape of all: a
    /// <c>FOR ALL … USING (true)</c> with no explicit WITH CHECK. Postgres then uses the USING
    /// expression as the new-row check and reports <c>with_check</c> as NULL, so an unconditional
    /// write policy was invisible to the check while a sibling policy satisfied the positive arm.
    /// <c>FOR DELETE … USING (true)</c> is the same story — DELETE has no WITH CHECK at all.</item>
    /// </list>
    /// <para>
    /// Hence <see cref="PolicyPin.EffectiveCheck"/>: for ALL/INSERT/UPDATE the new-row check is
    /// <c>with_check ?? qual</c>, and for DELETE it is <c>qual</c>. Reading only the column named
    /// with_check is what let the NULL case through.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, PolicyPin[]> ExpectedPlatformPolicies = new(StringComparer.Ordinal)
    {
        // An organization reads its OWN rows (or platform reads all); every write is platform-only.
        ["entitlements"] =
        [
            new("entitlements_org_read", "SELECT", "{public}", OrgOrPlatform, EffectiveCheck: null),
            new("entitlements_platform_write", "ALL", "{public}", PlatformGate, PlatformGate),
        ],
        ["capability_cohorts"] =
        [
            new("capability_cohorts_org_read", "SELECT", "{public}", OrgOrPlatform, EffectiveCheck: null),
            new("capability_cohorts_platform_write", "ALL", "{public}", PlatformGate, PlatformGate),
        ],

        // Readable anywhere — the capability resolver reads a kill switch inside the ambient request
        // transaction, where platform scope is unavailable and unsafe to fake. Writes stay platform-only.
        ["feature_flags"] =
        [
            new("feature_flags_read", "SELECT", "{public}", Unconditional, EffectiveCheck: null),
            new("feature_flags_platform_write", "ALL", "{public}", PlatformGate, PlatformGate),
        ],

        // Platform plane both ways: who granted what to whom is never visible in a tenant session.
        ["platform_audit_events"] =
        [
            new("platform_audit_events_platform_only", "ALL", "{public}", PlatformGate, PlatformGate),
        ],
    };

    /// <summary>
    /// The platform tables' foreign keys, pinned by their full Postgres definition (ADR-028).
    /// <para>
    /// These two are the ONLY foreign keys to <c>orgs</c> in the entire migration history, so nothing
    /// else in the suite would notice their loss — and the EF model cannot notice either. <c>Org</c>
    /// lives in the host, so a module's entity configuration cannot name it, and the host was
    /// deliberately not made to configure a module's entity (see
    /// <c>M8_ReconcileCapabilitiesModelSnapshot</c>). The consequence is that neither the model nor
    /// the snapshot knows these constraints exist, which means EF's differ will never emit an
    /// operation about them — including when it should. This pin is the compensating control.
    /// </para>
    /// <para>
    /// <c>platform_audit_events.org_id</c> is deliberately absent: it carries NO foreign key, because
    /// deleting an org must not delete the record of what was done to it. An entry appearing here for
    /// that table would be a regression, and the "unexpected" arm catches it.
    /// </para>
    /// <para>
    /// <b>The two delete actions differ on purpose, and the pin is where that is visible.</b>
    /// <c>entitlements</c> is <c>RESTRICT</c> (<c>M8_RestrictOrgDeleteOnEntitlements</c>): grant rows
    /// are append-only EVENTS, so deleting an org must not erase the record of what it was entitled to
    /// — the same reasoning that gave <c>platform_audit_events</c> no foreign key at all.
    /// <c>capability_cohorts</c> stays <c>CASCADE</c>: cohort rows are mutable MEMBERSHIP, not history,
    /// and membership in a deleted org is meaningless. Making them consistent with each other would
    /// mean either resurrecting the history-destroying cascade or leaving dead membership rows behind
    /// a failed delete; the asymmetry is the correct answer, not an oversight.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Definition)[]> ExpectedPlatformForeignKeys =
        new(StringComparer.Ordinal)
        {
            ["entitlements"] =
            [
                ("fk_entitlements_orgs_org_id",
                 "FOREIGN KEY (org_id) REFERENCES orgs(id) ON DELETE RESTRICT"),
            ],
            ["capability_cohorts"] =
            [
                ("fk_capability_cohorts_orgs_org_id",
                 "FOREIGN KEY (org_id) REFERENCES orgs(id) ON DELETE CASCADE"),
            ],
            ["feature_flags"] = [],
            ["platform_audit_events"] = [],
        };

    /// <summary>
    /// UNIQUE indexes on the platform tables, pinned. Unlike a plain index these carry semantics:
    /// <c>ux_entitlements_org_capability_effective_at</c> is what makes "the latest entitlement row
    /// per (org, capability)" a well-defined read, by making the tie impossible rather than ranking
    /// it — see <c>M8_AddEntitlementGrantUniqueness</c>. Dropping it would not fail a single query;
    /// it would just make the resolver's answer depend on physical row order.
    /// <para>
    /// Non-unique indexes are performance, not correctness, and are deliberately not pinned.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Columns)[]> ExpectedPlatformUniqueIndexes =
        new(StringComparer.Ordinal)
        {
            ["entitlements"] =
            [
                ("ux_entitlements_org_capability_effective_at", "org_id, capability, effective_at"),
            ],
            ["capability_cohorts"] = [],
            ["feature_flags"] = [],
            ["platform_audit_events"] = [],
        };

    /// <param name="Qual">Normalized <c>USING</c>. Null for an INSERT-only policy, which has none.</param>
    /// <param name="EffectiveCheck">
    /// The predicate a NEW row must satisfy: <c>with_check ?? qual</c> for ALL/INSERT/UPDATE,
    /// <c>qual</c> for DELETE, and null for SELECT, which admits no rows.
    /// </param>
    private sealed record PolicyPin(string Name, string Command, string Roles, string? Qual, string? EffectiveCheck);

    /// <summary>
    /// Identity-class tables (§C.3 / pitfall E6): exempt from RLS even though <c>asp_net_users</c>
    /// carries an <c>org_id</c> — authentication must work before any organization context exists, so user
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
        var seenPlatformTables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, rowSecurity, forceRowSecurity) in tables)
        {
            if (IdentityTables.Contains(name))
            {
                continue; // identity-class — protected by app logic, not RLS
            }

            // Positive assertion, deliberately outside the org_id branch below: feature_flags has no
            // org_id, so neither the org-scoped arm nor the allowlist arm would ever inspect its RLS.
            // The policy predicates themselves are pinned separately, in
            // Platform_table_policies_match_their_pinned_definitions.
            if (ExpectedPlatformPolicies.ContainsKey(name))
            {
                seenPlatformTables.Add(name);

                if (!rowSecurity || !forceRowSecurity)
                {
                    failures.Add($"{name}: platform table but RLS not ENABLEd+FORCEd " +
                                 $"(relrowsecurity={rowSecurity}, relforcerowsecurity={forceRowSecurity}).");
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
        seenPlatformTables.ShouldBe(ExpectedPlatformPolicies.Keys, ignoreOrder: true);

        failures.ShouldBeEmpty(failures.Count == 0 ? "" : string.Join(Environment.NewLine, failures));

        // Sanity: the guard is actually looking at our schema, not an empty catalog.
        orgScoped.ShouldContain("audit_events");
    }

    /// <summary>
    /// The platform tables' policies, pinned exactly (see <see cref="ExpectedPlatformPolicies"/>).
    /// An unexpected policy fails rather than passing unless it trips a heuristic — that inversion is
    /// the whole point, because every heuristic tried so far had a blind spot adjacent to the hole it
    /// was guarding.
    /// </summary>
    [Fact]
    public async Task Platform_table_policies_match_their_pinned_definitions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);

        var policies = await ReadPoliciesAsync(conn, ct);
        var failures = new List<string>();

        foreach (var (table, expected) in ExpectedPlatformPolicies)
        {
            var actual = policies
                .Where(p => p.Table == table)
                .Select(p => new PolicyPin(p.Name, p.Command, p.Roles, p.Qual, EffectiveCheckOf(p)))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToList();

            foreach (var extra in actual.Where(a => expected.All(e => e.Name != a.Name)))
            {
                failures.Add($"{table}: UNEXPECTED policy {Describe(extra)}. If it is intentional, add it " +
                             "to ExpectedPlatformPolicies and say why in the same change.");
            }

            foreach (var want in expected)
            {
                var got = actual.SingleOrDefault(a => a.Name == want.Name);
                if (got is null)
                {
                    failures.Add($"{table}: MISSING policy {want.Name} — expected {Describe(want)}.");
                }
                else if (got != want)
                {
                    failures.Add($"{table}: policy {want.Name} DRIFTED.{Environment.NewLine}" +
                                 $"  expected {Describe(want)}{Environment.NewLine}" +
                                 $"  actual   {Describe(got)}");
                }
            }
        }

        failures.ShouldBeEmpty(failures.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The platform tables' foreign keys, pinned exactly (see
    /// <see cref="ExpectedPlatformForeignKeys"/>). Both directions matter: a missing FK means an
    /// entitlement can name an org that does not exist, and an unexpected one on
    /// <c>platform_audit_events</c> would silently make the platform audit trail deletable by
    /// deleting an org.
    /// </summary>
    [Fact]
    public async Task Platform_table_foreign_keys_match_their_pinned_definitions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);

        var actual = await ReadForeignKeysAsync(conn, ct);
        var failures = new List<string>();

        foreach (var (table, expected) in ExpectedPlatformForeignKeys)
        {
            var got = actual.Where(f => f.Table == table).ToList();

            foreach (var extra in got.Where(g => expected.All(e => e.Name != g.Name)))
            {
                failures.Add($"{table}: UNEXPECTED foreign key {extra.Name} — {extra.Definition}. " +
                             "If it is intentional, add it to ExpectedPlatformForeignKeys and say why.");
            }

            foreach (var (name, definition) in expected)
            {
                var match = got.Where(g => g.Name == name).Select(g => g.Definition).SingleOrDefault();
                if (match is null)
                {
                    failures.Add($"{table}: MISSING foreign key {name} — expected {definition}. " +
                                 "These are the only FKs to orgs in the whole migration history, and " +
                                 "the EF model does not carry them, so nothing else would catch this.");
                }
                else if (!string.Equals(match, definition, StringComparison.Ordinal))
                {
                    failures.Add($"{table}: foreign key {name} DRIFTED.{Environment.NewLine}" +
                                 $"  expected {definition}{Environment.NewLine}" +
                                 $"  actual   {match}");
                }
            }
        }

        failures.ShouldBeEmpty(failures.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The platform tables' UNIQUE indexes, pinned (see <see cref="ExpectedPlatformUniqueIndexes"/>).
    /// Primary-key indexes are excluded — they are pinned implicitly by the table's key.
    /// </summary>
    [Fact]
    public async Task Platform_table_unique_indexes_match_their_pinned_definitions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);

        var actual = await ReadUniqueIndexesAsync(conn, ct);
        var failures = new List<string>();

        foreach (var (table, expected) in ExpectedPlatformUniqueIndexes)
        {
            var got = actual.Where(i => i.Table == table).ToList();

            foreach (var extra in got.Where(g => expected.All(e => e.Name != g.Name)))
            {
                failures.Add($"{table}: UNEXPECTED unique index {extra.Name} ({extra.Columns}). " +
                             "A new uniqueness rule changes which writes are legal — add it to " +
                             "ExpectedPlatformUniqueIndexes and say why.");
            }

            foreach (var (name, columns) in expected)
            {
                var match = got.Where(g => g.Name == name).Select(g => g.Columns).SingleOrDefault();
                if (match is null)
                {
                    failures.Add($"{table}: MISSING unique index {name} on ({columns}). Dropping it " +
                                 "fails no query — it just makes the 'latest row' read depend on " +
                                 "physical row order (M8_AddEntitlementGrantUniqueness).");
                }
                else if (!string.Equals(match, columns, StringComparison.Ordinal))
                {
                    failures.Add($"{table}: unique index {name} DRIFTED.{Environment.NewLine}" +
                                 $"  expected ({columns}){Environment.NewLine}" +
                                 $"  actual   ({match})");
                }
            }
        }

        failures.ShouldBeEmpty(failures.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private static async Task<List<(string Table, string Name, string Definition)>> ReadForeignKeysAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT c.conrelid::regclass::text, c.conname, pg_get_constraintdef(c.oid) " +
            "FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace " +
            "WHERE n.nspname = 'public' AND c.contype = 'f'", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = new List<(string, string, string)>();
        while (await reader.ReadAsync(ct))
        {
            result.Add((reader.GetString(0), reader.GetString(1), Normalize(reader.GetString(2))!));
        }

        return result;
    }

    private static async Task<List<(string Table, string Name, string Columns)>> ReadUniqueIndexesAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT t.relname,
                   i.relname,
                   (SELECT string_agg(a.attname, ', ' ORDER BY k.ord)
                      FROM unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord)
                      JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum)
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = 'public' AND ix.indisunique AND NOT ix.indisprimary
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = new List<(string, string, string)>();
        while (await reader.ReadAsync(ct))
        {
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }

    /// <summary>
    /// The predicate a NEW row must satisfy. A <c>FOR ALL</c>/<c>FOR UPDATE</c> policy written with
    /// only <c>USING</c> reuses that expression as its new-row check and reports <c>with_check</c> as
    /// NULL, so reading the with_check column alone would treat <c>FOR ALL USING (true)</c> as having
    /// no write rule at all. DELETE admits no new row, so its gate is the <c>USING</c>; SELECT has no
    /// write path.
    /// </summary>
    private static string? EffectiveCheckOf((string Table, string Name, string Command, string Roles, string? Qual, string? WithCheck) policy) =>
        policy.Command switch
        {
            "ALL" or "INSERT" or "UPDATE" => policy.WithCheck ?? policy.Qual,
            "DELETE" => policy.Qual,
            _ => null, // SELECT
        };

    private static string Describe(PolicyPin pin) =>
        $"[{pin.Command} TO {pin.Roles}] USING {pin.Qual ?? "<none>"} CHECK {pin.EffectiveCheck ?? "<none>"}";

    /// <summary>
    /// Collapses runs of whitespace so that reformatting the SQL in a migration cannot fail the pin.
    /// Postgres re-prints predicates from the parse tree, so this is belt-and-braces, but the pin is
    /// meant to fail on meaning, not on layout.
    /// </summary>
    private static string? Normalize(string? sql) =>
        sql is null ? null : string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static async Task<List<(string Table, string Name, string Command, string Roles, string? Qual, string? WithCheck)>>
        ReadPoliciesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT tablename, policyname, cmd, roles::text, qual, with_check " +
            "FROM pg_policies WHERE schemaname = 'public'", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = new List<(string, string, string, string, string?, string?)>();
        while (await reader.ReadAsync(ct))
        {
            result.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                Normalize(reader.IsDBNull(4) ? null : reader.GetString(4)),
                Normalize(reader.IsDBNull(5) ? null : reader.GetString(5))));
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
