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

    /// <summary>
    /// Foreign keys between two org-scoped tables that are deliberately NOT organization-constrained,
    /// keyed by <c>"table.constraint"</c> with the reason they may stay that way. Empty on purpose:
    /// every such key should pair <c>org_id</c> on both sides, and an entry here is a documented
    /// exception, never a way to quiet the guard. Adding one means arguing why a row in this table may
    /// legitimately point at another organization's row.
    /// </summary>
    private static readonly Dictionary<string, string> OrgUnconstrainedForeignKeyAllowlist =
        new(StringComparer.Ordinal);

    // The three predicates the capability migration emits, as Postgres normalizes and stores them:
    // it re-prints the parse tree, so 'app.platform' becomes 'app.platform'::text and so on. These
    // are compared literally, which is the point — see ExpectedPlatformPolicies.
    private const string PlatformGate = "(current_setting('app.platform'::text, true) = 'on'::text)";

    private const string OrgOrPlatform =
        "((org_id = (NULLIF(current_setting('app.org_id'::text, true), ''::text))::uuid) " +
        "OR (current_setting('app.platform'::text, true) = 'on'::text))";

    private const string Unconditional = "true";

    // ── The persona gate (#314, ADR-048) ─────────────────────────────────────────────────────────
    // Fragments of the predicates the persona migration emits, as Postgres re-prints them. The pins
    // below are written with {NAME} placeholders for these, so each grant reads as what it admits
    // rather than as a wall of casts; Expand substitutes them before the literal comparison.
    private static readonly (string Name, string Sql)[] PersonaFragments =
    [
        ("GATE_OR_PLATFORM", "({GATE} OR (current_setting('app.platform'::text, true) = 'on'::text))"),
        ("GATE", "COALESCE((( SELECT current_setting('app.persona'::text, true) AS current_setting) = ANY (ARRAY['staff'::text, 'system'::text])), false)"),
        ("IS_OWNER", "(( SELECT current_setting('app.persona'::text, true) AS current_setting) = 'owner'::text)"),
        ("IS_TENANT", "(( SELECT current_setting('app.persona'::text, true) AS current_setting) = 'tenant'::text)"),
        // The caller's owner/tenant through an active link AND a non-system Directory row (review L2).
        ("MY_OWNERS", "( SELECT oa.owner_id FROM (owner_access oa JOIN owners o ON ((o.id = oa.owner_id))) WHERE ((oa.user_id = {MY_USER}) AND (oa.revoked_at IS NULL) AND (NOT o.is_system)))"),
        ("MY_TENANTS", "( SELECT ra.tenant_id FROM (resident_access ra JOIN tenants t ON ((t.id = ra.tenant_id))) WHERE ((ra.user_id = {MY_USER}) AND (ra.revoked_at IS NULL) AND (NOT t.is_system)))"),
        // The link alone: only the owners/tenants grants use it, checking is_system on their own row,
        // because joining their own table would make the policy recurse.
        ("MY_OWNER_LINKS", "( SELECT oa.owner_id FROM owner_access oa WHERE ((oa.user_id = {MY_USER}) AND (oa.revoked_at IS NULL)))"),
        ("MY_TENANT_LINKS", "( SELECT ra.tenant_id FROM resident_access ra WHERE ((ra.user_id = {MY_USER}) AND (ra.revoked_at IS NULL)))"),
        // A portal user's own password or MFA-enrolment event (review L3).
        ("OWN_ACCOUNT_SECURITY", "((actor_kind)::text = 'user'::text) AND (actor_user_id = {MY_USER}) " +
            "AND ((entity_type)::text = 'account-security'::text) AND (entity_id = {MY_USER}) " +
            "AND ((action)::text = ANY ((ARRAY['password-changed'::character varying, 'mfa-enrolled'::character varying])::text[]))"),
        ("MY_USER", "(NULLIF(current_setting('app.user_id'::text, true), ''::text))::uuid"),
    ];

    /// <summary>The capability tables' gate also admits the platform plane, which has no org or persona.</summary>
    private static readonly HashSet<string> PlatformGatedTables = new(StringComparer.Ordinal)
    {
        "entitlements", "capability_cohorts", "platform_audit_events",
    };

    /// <summary>
    /// The portal grant table, pinned: for each table a portal persona may reach, the exact USING
    /// (reads, and the rows <c>FOR SHARE</c> may lock) and WITH CHECK (new rows) of its
    /// <c>{table}_persona</c> policy. A table absent here must carry the deny-by-default gate in both.
    /// <para>
    /// This is the one place a widened grant has to be written down to pass. The isolation behaviour
    /// is proven in <c>PersonaIsolationTests</c>; this pins the text, so a grant cannot drift wider in
    /// a way the fixture happens not to exercise.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, (string Using, string Check)> PersonaGrants = new(StringComparer.Ordinal)
    {
        // The link tables resolve to the caller's own rows by user id alone — no subquery, so no
        // policy recursion through themselves.
        ["owner_access"] = ("({GATE} OR ({IS_OWNER} AND (user_id = {MY_USER})))", "{GATE}"),
        ["resident_access"] = ("({GATE} OR ({IS_TENANT} AND (user_id = {MY_USER})))", "{GATE}"),

        // Owner reads: its non-system owner row, its lines and their entries, the properties on its
        // lines or currently its own, its issued statements.
        ["owners"] = ("({GATE} OR ({IS_OWNER} AND (NOT is_system) AND (id IN {MY_OWNER_LINKS})))", "{GATE}"),
        ["statement_artifacts"] = ("({GATE} OR ({IS_OWNER} AND (owner_id IN {MY_OWNERS})))", "{GATE}"),

        // Tenant reads: its tenant row, its lease and what the lease names, its ownership history
        // (the payment eligibility check resolves the effective owner), the fixture bank account.
        ["tenants"] = ("({GATE} OR ({IS_TENANT} AND (NOT is_system) AND (id IN {MY_TENANT_LINKS})))", "{GATE}"),
        ["lease_lite"] = ("({GATE} OR ({IS_TENANT} AND (tenant_id IN {MY_TENANTS})))", "{GATE}"),
        ["units"] = ("({GATE} OR ({IS_TENANT} AND (id IN ( SELECT l.unit_id FROM lease_lite l WHERE (l.tenant_id IN {MY_TENANTS})))))", "{GATE}"),
        ["property_ownership_transfers"] =
            ("({GATE} OR ({IS_TENANT} AND (property_id IN ( SELECT u.property_id FROM units u WHERE (u.id IN ( SELECT l.unit_id FROM lease_lite l WHERE (l.tenant_id IN {MY_TENANTS})))))))", "{GATE}"),
        ["accounts"] = ("({GATE} OR ({IS_TENANT} AND (id IN ( SELECT jl.account_id FROM journal_lines jl WHERE (jl.tenant_id IN {MY_TENANTS})))))", "{GATE}"),
        ["bank_accounts"] = ("({GATE} OR ({IS_TENANT} AND (EXISTS {MY_TENANTS}) AND (id IN ( SELECT pf.bank_id FROM payment_fixtures pf))))", "{GATE}"),
        ["payment_fixtures"] = ("({GATE} OR ({IS_TENANT} AND (EXISTS {MY_TENANTS})))", "{GATE}"),
        ["payment_observations"] =
            ("({GATE} OR ({IS_TENANT} AND ((provider_id)::text IN ( SELECT po.provider_id FROM payment_operations po WHERE (po.tenant_id IN {MY_TENANTS})))))", "{GATE}"),

        // The one portal table with an insert grant besides the audit log: a tenant submits its own
        // payment, as itself, and only as a fresh request against the org's fixture (review M1) —
        // every worker-owned column at its initial value, above all provider_id, which the
        // observations grant keys on. No UPDATE or DELETE — the companions see to it.
        ["payment_operations"] = (
            "({GATE} OR ({IS_TENANT} AND (tenant_id IN {MY_TENANTS})))",
            "({GATE} OR ({IS_TENANT} AND (tenant_id IN {MY_TENANTS}) AND (user_id = {MY_USER}) " +
            "AND ((status)::text = 'Requested'::text) AND (reason IS NULL) AND (provider_id IS NULL) AND (journal_id IS NULL) " +
            "AND (processed_count = 0) AND (attempts = 0) AND (last_attempt_at IS NULL) AND (lease_until IS NULL) " +
            "AND (lease_claim_id IS NULL) AND (EXISTS ( SELECT 1 FROM payment_fixtures pf WHERE ((pf.id = payment_operations.generation) " +
            "AND (pf.bank_id = payment_operations.bank_id) AND ((pf.account)::text = (payment_operations.account)::text))))))"),

        // Shared by both personas.
        ["journal_lines"] = ("({GATE} OR (({IS_OWNER} AND (owner_id IN {MY_OWNERS})) OR ({IS_TENANT} AND (tenant_id IN {MY_TENANTS}))))", "{GATE}"),
        ["journal_entries"] = (
            "({GATE} OR (({IS_OWNER} AND (id IN ( SELECT jl.entry_id FROM journal_lines jl WHERE (jl.owner_id IN {MY_OWNERS})))) " +
            "OR ({IS_TENANT} AND (id IN ( SELECT jl.entry_id FROM journal_lines jl WHERE (jl.tenant_id IN {MY_TENANTS}))))))",
            "{GATE}"),
        ["properties"] = (
            "({GATE} OR (({IS_OWNER} AND ((owner_id IN {MY_OWNERS}) OR (id IN ( SELECT jl.property_id FROM journal_lines jl WHERE (jl.owner_id IN {MY_OWNERS}))))) " +
            "OR ({IS_TENANT} AND (id IN ( SELECT u.property_id FROM units u WHERE (u.id IN ( SELECT l.unit_id FROM lease_lite l WHERE (l.tenant_id IN {MY_TENANTS}))))))))",
            "{GATE}"),
        ["org_settings"] = (
            "({GATE} OR (({IS_OWNER} AND (EXISTS {MY_OWNERS})) OR ({IS_TENANT} AND (EXISTS {MY_TENANTS}))))",
            "{GATE}"),

        // Write-only: a portal persona records its own account-security events (both personas) and
        // its payment submissions (tenant), attributed to itself, and reads none of it back.
        ["audit_events"] = (
            "{GATE}",
            "({GATE} OR (({IS_TENANT} AND (({OWN_ACCOUNT_SECURITY}) OR (((actor_kind)::text = 'user'::text) " +
            "AND (actor_user_id = {MY_USER}) AND ((entity_type)::text = 'payment_operations'::text) AND ((action)::text = 'insert'::text)))) " +
            "OR ({IS_OWNER} AND {OWN_ACCOUNT_SECURITY})))"),
    };

    private const string OrgIsolation = "(org_id = (NULLIF(current_setting('app.org_id'::text, true), ''::text))::uuid)";

    /// <summary>Substitutes the <see cref="PersonaFragments"/> placeholders until none remain.</summary>
    private static string Expand(string template)
    {
        var result = template;
        for (var pass = 0; pass < 4 && result.Contains('{', StringComparison.Ordinal); pass++)
        {
            foreach (var (name, sql) in PersonaFragments)
            {
                result = result.Replace("{" + name + "}", sql, StringComparison.Ordinal);
            }
        }

        return result;
    }

    private static string GateFor(string table) =>
        Expand(PlatformGatedTables.Contains(table) ? "{GATE_OR_PLATFORM}" : "{GATE}");

    /// <summary>The three persona-gate policies a table must carry, fully pinned.</summary>
    private static PersonaPolicyPin[] ExpectedPersonaGate(string table)
    {
        var gate = GateFor(table);
        var (read, insert) = PersonaGrants.TryGetValue(table, out var grant)
            ? (Expand(grant.Using), Expand(grant.Check))
            : (gate, gate);

        return
        [
            new($"{table}_persona", "RESTRICTIVE", "ALL", "{public}", read, insert),
            new($"{table}_persona_no_update", "RESTRICTIVE", "UPDATE", "{public}", Unconditional, gate),
            new($"{table}_persona_no_delete", "RESTRICTIVE", "DELETE", "{public}", gate, null),
        ];
    }

    /// <param name="WithCheck">The stored <c>with_check</c>, not the effective one: the gate always
    /// writes it out explicitly, so a null here on an ALL/UPDATE policy is itself a finding.</param>
    private sealed record PersonaPolicyPin(
        string Name, string Kind, string Command, string Roles, string? Using, string? WithCheck);

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
        // Each org-scoped platform table also carries the persona gate (#314), pinned in full by
        // Every_org_scoped_table_carries_exactly_its_pinned_policies; listed here so this pin, which
        // rejects any policy it does not name, stays exhaustive.
        ["entitlements"] =
        [
            new("entitlements_org_read", "SELECT", "{public}", OrgOrPlatform, EffectiveCheck: null),
            new("entitlements_platform_write", "ALL", "{public}", PlatformGate, PlatformGate),
            .. PersonaGatePins("entitlements"),
        ],
        ["capability_cohorts"] =
        [
            new("capability_cohorts_org_read", "SELECT", "{public}", OrgOrPlatform, EffectiveCheck: null),
            new("capability_cohorts_platform_write", "ALL", "{public}", PlatformGate, PlatformGate),
            .. PersonaGatePins("capability_cohorts"),
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
            .. PersonaGatePins("platform_audit_events"),
        ],
    };

    /// <summary>The persona gate in this pin's shape: the effective new-row check, not the stored one.</summary>
    private static IEnumerable<PolicyPin> PersonaGatePins(string table) =>
        ExpectedPersonaGate(table).Select(p => new PolicyPin(
            p.Name, p.Command, p.Roles, p.Using,
            p.Command == "DELETE" ? p.Using : p.WithCheck));

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
    /// Every org-scoped table carries exactly its pinned policy set (#314, ADR-048): the unchanged
    /// org (or platform) policies, and the three RESTRICTIVE persona-gate policies with the predicates
    /// in <see cref="PersonaGrants"/>, or the deny-by-default gate for a table that has no grant.
    /// <para>
    /// Walks the live catalog, so a new org-scoped table fails here until its migration gates it —
    /// which <c>Rls.EnableOrgRls</c> does on its own. Exhaustive in both directions: an extra
    /// RESTRICTIVE policy would silently narrow a table (before this test it passed unnoticed), and
    /// an extra PERMISSIVE one would widen it, since permissive policies OR together.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_org_scoped_table_carries_exactly_its_pinned_policies()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);

        var orgScoped = (await ReadNamesAsync(conn,
                "SELECT table_name FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND column_name = 'org_id'", ct))
            .Where(table => !IdentityTables.Contains(table))
            .ToHashSet(StringComparer.Ordinal);
        var policies = await ReadPolicyShapesAsync(conn, ct);
        var failures = new List<string>();

        foreach (var table in orgScoped.Order(StringComparer.Ordinal))
        {
            var expected = BasePolicies(table).Concat(ExpectedPersonaGate(table)).ToList();
            var actual = policies.Where(p => p.Table == table).Select(p => p.Pin).ToList();

            foreach (var extra in actual.Where(a => expected.All(e => e.Name != a.Name)))
            {
                failures.Add($"{table}: UNEXPECTED {extra.Kind} policy {Describe(extra)}.");
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

        // A pinned grant for a table that no longer exists would be a pin guarding nothing.
        foreach (var stale in PersonaGrants.Keys.Where(table => !orgScoped.Contains(table)))
        {
            failures.Add($"{stale}: has a pinned persona grant but is not an org-scoped table.");
        }

        failures.ShouldBeEmpty(failures.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, failures));

        // Sanity: the walk covered the schema it exists for.
        orgScoped.Count.ShouldBeGreaterThanOrEqualTo(37);
        orgScoped.ShouldContain("journal_lines");
        orgScoped.ShouldContain("entitlements");
    }

    /// <summary>The permissive policies each org-scoped table had before the persona gate, unchanged.</summary>
    private static IEnumerable<PersonaPolicyPin> BasePolicies(string table) =>
        ExpectedPlatformPolicies.TryGetValue(table, out var platform)
            ? platform
                .Where(p => !p.Name.StartsWith($"{table}_persona", StringComparison.Ordinal))
                .Select(p => new PersonaPolicyPin(
                    p.Name, "PERMISSIVE", p.Command, p.Roles, p.Qual,
                    p.Command == "SELECT" ? null : p.EffectiveCheck))
            : [new($"{table}_org_isolation", "PERMISSIVE", "ALL", "{public}", OrgIsolation, OrgIsolation)];

    private static string Describe(PersonaPolicyPin pin) =>
        $"[{pin.Kind} {pin.Command} TO {pin.Roles}] USING {pin.Using ?? "<none>"} CHECK {pin.WithCheck ?? "<none>"}";

    private static async Task<List<(string Table, PersonaPolicyPin Pin)>> ReadPolicyShapesAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT tablename, policyname, permissive, cmd, roles::text, qual, with_check " +
            "FROM pg_policies WHERE schemaname = 'public'", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = new List<(string, PersonaPolicyPin)>();
        while (await reader.ReadAsync(ct))
        {
            result.Add((reader.GetString(0), new PersonaPolicyPin(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                Normalize(reader.IsDBNull(5) ? null : reader.GetString(5)),
                Normalize(reader.IsDBNull(6) ? null : reader.GetString(6)))));
        }

        return result;
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

    /// <summary>
    /// Every foreign key whose referencing AND referenced table are both org-scoped must carry
    /// <c>org_id</c> on both sides of the constraint — <c>FOREIGN KEY (org_id, x_id) REFERENCES
    /// t (org_id, id)</c>, never <c>FOREIGN KEY (x_id) REFERENCES t (id)</c>.
    /// <para>
    /// Referential integrity is evaluated by the system, <i>bypassing</i> row-level security: a
    /// single-column key proves only that the referenced row exists in SOME organization, not in the
    /// referencing row's own. RLS cannot close that gap, because a policy sees one table at a time.
    /// So a cross-organization pointer written through any path that sets the wrong id — an import, a
    /// bulk run, a mis-parameterized command — is accepted by Postgres and only shows up later as a
    /// row that reads as missing under its own organization's context.
    /// </para>
    /// <para>
    /// The check walks the live catalog rather than a pinned list, so a table nobody thought of is
    /// covered the moment its migration lands. It also verifies the PAIRING, not merely that
    /// <c>org_id</c> appears in the key: the referenced column at <c>org_id</c>'s position must be the
    /// referenced table's own <c>org_id</c>, which is the property that actually forces the two
    /// organizations to be equal.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_foreign_key_between_org_scoped_tables_is_org_constrained()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);

        var offenders = await ReadOrgUnconstrainedForeignKeysAsync(conn, ct);

        var failures = offenders
            .Where(o => !OrgUnconstrainedForeignKeyAllowlist.ContainsKey($"{o.Table}.{o.Name}"))
            .Select(o => $"{o.Table}.{o.Name} -> {o.ReferencedTable}: {o.Definition}")
            .ToList();

        failures.ShouldBeEmpty(failures.Count == 0
            ? ""
            : $"{failures.Count} foreign key(s) between org-scoped tables do not constrain org_id, so " +
              "Postgres will accept a row pointing at another organization's row (FK checks bypass " +
              "RLS). Use .HasForeignKey(x => new { x.OrgId, x.SomeId })" +
              ".HasPrincipalKey(p => new { p.OrgId, p.Id }) and give the principal an " +
              "(org_id, id) alternate key." + Environment.NewLine +
              string.Join(Environment.NewLine, failures));

        // Sanity: an empty result is the pass condition above, so prove the catalog was actually
        // populated and not, say, filtered to nothing by a schema-name typo. Deliberately asserted on
        // table names rather than constraint names, which EF rewrites whenever a key's columns change.
        var orgScoped = await ReadNamesAsync(conn,
            "SELECT table_name FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND column_name = 'org_id'", ct);
        orgScoped.ShouldContain("units");
        (await ReadForeignKeysAsync(conn, ct)).ShouldNotBeEmpty();
    }

    /// <summary>
    /// Foreign keys where both ends are org-scoped tables but the constraint does not pair the two
    /// <c>org_id</c> columns. See
    /// <see cref="Every_foreign_key_between_org_scoped_tables_is_org_constrained"/>.
    /// <para>
    /// <c>hangfire</c> is excluded by name for the same reason the rest of this file only walks
    /// <c>public</c>: its objects are the scheduler's own, owned by the runtime role, never
    /// org-scoped and never EF-migration territory (ADR-001). Every other schema is in scope, so a
    /// future org-scoped table outside <c>public</c> cannot slip past.
    /// </para>
    /// </summary>
    private static async Task<List<(string Table, string Name, string ReferencedTable, string Definition)>>
        ReadOrgUnconstrainedForeignKeysAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            WITH org_tables AS (
                SELECT c.oid, c.relname
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'org_id'
                                   AND a.attnum > 0 AND NOT a.attisdropped
                WHERE c.relkind = 'r'
                  AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'hangfire')
            )
            SELECT src.relname, con.conname, tgt.relname, pg_get_constraintdef(con.oid)
            FROM pg_constraint con
            JOIN org_tables src ON src.oid = con.conrelid
            JOIN org_tables tgt ON tgt.oid = con.confrelid
            WHERE con.contype = 'f'
              AND NOT EXISTS (
                    SELECT 1
                    FROM generate_subscripts(con.conkey, 1) AS i
                    WHERE con.conkey[i] = (SELECT a.attnum FROM pg_attribute a
                                           WHERE a.attrelid = con.conrelid
                                             AND a.attname = 'org_id' AND NOT a.attisdropped)
                      AND con.confkey[i] = (SELECT a.attnum FROM pg_attribute a
                                            WHERE a.attrelid = con.confrelid
                                              AND a.attname = 'org_id' AND NOT a.attisdropped))
            ORDER BY src.relname, con.conname
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = new List<(string, string, string, string)>();
        while (await reader.ReadAsync(ct))
        {
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        Normalize(reader.GetString(3))!));
        }

        return result;
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
