using Microsoft.EntityFrameworkCore.Migrations;

namespace LeaseBook.Web.Persistence;

/// <summary>
/// The single helper every org-scoped table's migration uses to get RLS right (CLAUDE.md;
/// §C.3). One call emits ENABLE + FORCE ROW LEVEL SECURITY and the bare-equality org-isolation
/// policy with both USING and WITH CHECK. WP-05's schema guard fails CI if any <c>org_id</c>
/// table is missing this. Append-only tables additionally call <see cref="RevokeAppendOnly"/>.
/// <para>
/// Every org-scoped helper also closes the table to the portal personas (#314, ADR-048) through
/// <see cref="ApplyPersonaGate"/>, so a new table is invisible to a tenant or owner until a later
/// migration writes that table's grant.
/// </para>
/// </summary>
public static class Rls
{
    public static void EnableOrgRls(this MigrationBuilder migrationBuilder, string table)
    {
        // NULLIF(..., '') is load-bearing: a custom GUC placeholder that has been SET LOCAL once in
        // a session reverts to '' (empty string), not NULL, after the transaction ends. Casting
        // ''::uuid raises 22P02 instead of failing closed, so we map empty → NULL → no rows match.
        //
        // Explicit GRANT is intentional defense-in-depth for the RLS security boundary.
        // bootstrap.sql already covers every migrator-created table via
        //   ALTER DEFAULT PRIVILEGES FOR ROLE leasebook_migrator IN SCHEMA public ...
        // so the default privileges apply here too — that is why all M1–M5 org-scoped tables work
        // without an explicit grant. These explicit grants are a deliberate second layer: they make
        // the runtime-role permissions resilient to future bootstrap changes and document the
        // intended privilege set directly on each table. They are idempotent and harmless, carrying
        // the same enumerated privileges as the default-privileges block.
        migrationBuilder.Sql($"""
            ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
            CREATE POLICY {table}_org_isolation ON {table}
              FOR ALL
              USING (org_id = NULLIF(current_setting('app.org_id', true), '')::uuid)
              WITH CHECK (org_id = NULLIF(current_setting('app.org_id', true), '')::uuid);
            GRANT SELECT, INSERT, UPDATE, DELETE ON {table} TO leasebook_app;
            GRANT SELECT ON {table} TO leasebook_ops;
            """);
        migrationBuilder.ApplyPersonaGate(table);
    }

    /// <summary>
    /// Organization-readable, platform-writable (ADR-028). Platform tables carry org_id because they are
    /// data ABOUT orgs: an organization may READ its own entitlements and cohort rows, but only the
    /// platform plane may WRITE them. The escape is a GUC set by <c>PlatformScopedExecutor</c> and
    /// nowhere else.
    /// <para>
    /// Deliberately TWO policies, not one <c>FOR ALL</c>. A single policy whose WITH CHECK reads
    /// <c>org_id = my_org OR platform</c> lets an organization-plane transaction INSERT its own row — i.e.
    /// self-grant a paid capability. <see cref="RevokeAppendOnly"/> does not close that: it strips
    /// UPDATE/DELETE, not INSERT. Splitting read from write is what makes writes platform-only.
    /// </para>
    /// <para>
    /// How Postgres resolves the pair: permissive policies OR together within a command, and
    /// UPDATE/DELETE that read existing rows must additionally satisfy the SELECT policies. So
    /// SELECT ⇒ org OR platform (organization reads of their own rows keep working); INSERT ⇒ the write
    /// policy's WITH CHECK alone, so the organization plane gets 42501; UPDATE/DELETE ⇒ filtered by the
    /// write policy's USING, so the organization plane affects zero rows.
    /// </para>
    /// <para>
    /// Why an escape rather than no RLS: a path that forgets to open platform scope returns ZERO
    /// rows instead of every org's rows. Visible emptiness beats a silent cross-organization leak. It also
    /// keeps the table inside SchemaGuardTests' normal org-scoped arm — no new exemption class.
    /// </para>
    /// </summary>
    public static void EnableOrgRlsWithPlatformEscape(this MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"""
            ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
            CREATE POLICY {table}_org_read ON {table}
              FOR SELECT
              USING (org_id = NULLIF(current_setting('app.org_id', true), '')::uuid
                     OR current_setting('app.platform', true) = 'on');
            CREATE POLICY {table}_platform_write ON {table}
              FOR ALL
              USING (current_setting('app.platform', true) = 'on')
              WITH CHECK (current_setting('app.platform', true) = 'on');
            GRANT SELECT, INSERT, UPDATE, DELETE ON {table} TO leasebook_app;
            GRANT SELECT ON {table} TO leasebook_ops;
            """);
        migrationBuilder.ApplyPersonaGate(table, platformEscape: true);
    }

    /// <summary>
    /// Readable by anyone, writable only by the platform plane (ADR-028). This is <c>feature_flags</c>:
    /// deployment configuration, not organization data. The property worth protecting is that
    /// organization-plane work cannot <b>toggle</b> a flag, not that it cannot read one — a flag's effects surface as UI behavior
    /// anyway.
    /// <para>
    /// Why reads must be ungated: the capability resolver reads flags <i>inside the ambient request
    /// transaction</i>, so a money-path kill switch takes effect immediately rather than waiting out a
    /// cache TTL. <c>PlatformScopedExecutor</c> cannot nest inside that transaction (it opens its own),
    /// and setting the platform GUC transaction-locally in there is not an option either: it would
    /// persist to end of transaction and leave the rest of the request running with platform scope,
    /// silently defeating org isolation on <c>entitlements</c> and <c>capability_cohorts</c>.
    /// </para>
    /// <para>
    /// Same two-policy split as <see cref="EnableOrgRlsWithPlatformEscape"/>, and for the same reason:
    /// a single <c>FOR ALL</c> policy with an unconditional predicate would make the table writable by
    /// anyone. The read policy is <c>USING (true)</c> because the table has no <c>org_id</c> to key on —
    /// a flag is a property of the deployment. ENABLE + FORCE still apply, so the write gate binds the
    /// schema owner too.
    /// </para>
    /// </summary>
    public static void EnableGlobalReadPlatformWriteRls(this MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"""
            ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
            CREATE POLICY {table}_read ON {table}
              FOR SELECT
              USING (true);
            CREATE POLICY {table}_platform_write ON {table}
              FOR ALL
              USING (current_setting('app.platform', true) = 'on')
              WITH CHECK (current_setting('app.platform', true) = 'on');
            GRANT SELECT, INSERT, UPDATE, DELETE ON {table} TO leasebook_app;
            GRANT SELECT ON {table} TO leasebook_ops;
            """);
    }

    /// <summary>
    /// Platform-plane only: no organization request can read or write these rows at all, whatever its org
    /// context. Used for <c>platform_audit_events</c>, which must never be visible inside an organization
    /// session — who granted what to whom is not organization-facing. Applies to org-scoped and global tables
    /// alike — it never mentions org_id, so it carries no requirement about that column.
    /// </summary>
    public static void EnablePlatformOnlyRls(this MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"""
            ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
            CREATE POLICY {table}_platform_only ON {table}
              FOR ALL
              USING (current_setting('app.platform', true) = 'on')
              WITH CHECK (current_setting('app.platform', true) = 'on');
            GRANT SELECT, INSERT, UPDATE, DELETE ON {table} TO leasebook_app;
            GRANT SELECT ON {table} TO leasebook_ops;
            """);
        migrationBuilder.ApplyPersonaGate(table, platformEscape: true);
    }

    /// <summary>
    /// The organization-wide personas, as the persona gate tests them. The <c>SELECT</c> wrapper makes
    /// Postgres evaluate the setting once per statement as an InitPlan instead of once per row, which
    /// is what keeps the gate off the staff read path's cost (docs/perf.md).
    /// </summary>
    public const string OrgWidePersona =
        "(SELECT current_setting('app.persona', true)) IN ('staff', 'system')";

    /// <summary>
    /// The gate for the capability tables (ADR-028): organization-wide personas, or the platform plane,
    /// which runs with no org and no persona at all and must keep working exactly as before.
    /// </summary>
    public const string OrgWidePersonaOrPlatform =
        "(" + OrgWidePersona + " OR current_setting('app.platform', true) = 'on')";

    /// <summary>
    /// The persona gate (#314, ADR-048): the database boundary <i>inside</i> an organization. Three
    /// RESTRICTIVE policies, so they AND with whatever permissive policy admits the row — the org
    /// policy is untouched and still decides which organization; the gate decides which slice of it.
    /// <list type="bullet">
    /// <item><c>{table}_persona</c> (FOR ALL) is the grant table: <c>staff</c> and <c>system</c>
    /// everywhere, a portal persona only through <paramref name="portalRead"/> (reads, and the row
    /// locks <c>FOR SHARE</c> takes) and <paramref name="portalInsert"/> (new rows), nothing for any
    /// other value — unset, <c>none</c>, or a string nobody defined. The WITH CHECK is always written
    /// out, never left to default to the USING, so a read grant can never become a write grant by
    /// omission.</item>
    /// <item><c>{table}_persona_no_update</c> (FOR UPDATE) admits a changed row only for the
    /// organization-wide personas. It is separate because one FOR ALL policy has one WITH CHECK for
    /// INSERT and UPDATE alike, so a portal insert grant would otherwise also let the persona rewrite
    /// the rows it inserted. Its USING is <c>true</c> so that <c>SELECT … FOR SHARE</c>, which applies
    /// UPDATE policies' USING, is still decided by the read grant alone.</item>
    /// <item><c>{table}_persona_no_delete</c> (FOR DELETE) admits deletion only for the
    /// organization-wide personas. It is separate because one FOR ALL policy has one USING for SELECT
    /// and DELETE alike, so a read grant would otherwise be a delete grant.</item>
    /// </list>
    /// <para>
    /// Idempotent — each policy is dropped first — because this is also how a grant changes: a later
    /// migration calls it again with the new predicates. Called with no grants it is deny-by-default,
    /// which is what every org-scoped helper above applies to a new table.
    /// </para>
    /// <para>
    /// Grants resolve the caller's owner or tenant at the database, from the active link row for
    /// <c>app.user_id</c>; no owner or tenant id is ever set by the application, so revoking a link
    /// takes effect on the next statement. See the <c>P2_PersonaRls</c> migration for the grant table.
    /// </para>
    /// </summary>
    public static void ApplyPersonaGate(
        this MigrationBuilder migrationBuilder,
        string table,
        string? portalRead = null,
        string? portalInsert = null,
        bool platformEscape = false)
    {
        var gate = platformEscape ? OrgWidePersonaOrPlatform : OrgWidePersona;
        var read = portalRead is null ? gate : $"{gate} OR ({portalRead})";
        var insert = portalInsert is null ? gate : $"{gate} OR ({portalInsert})";

        migrationBuilder.Sql($"""
            DROP POLICY IF EXISTS {table}_persona ON {table};
            DROP POLICY IF EXISTS {table}_persona_no_update ON {table};
            DROP POLICY IF EXISTS {table}_persona_no_delete ON {table};
            CREATE POLICY {table}_persona ON {table}
              AS RESTRICTIVE
              FOR ALL
              USING ({read})
              WITH CHECK ({insert});
            CREATE POLICY {table}_persona_no_update ON {table}
              AS RESTRICTIVE
              FOR UPDATE
              USING (true)
              WITH CHECK ({gate});
            CREATE POLICY {table}_persona_no_delete ON {table}
              AS RESTRICTIVE
              FOR DELETE
              USING ({gate});
            """);
    }

    /// <summary>Removes the persona gate from <paramref name="table"/> — the reversal of <see cref="ApplyPersonaGate"/>.</summary>
    public static void DropPersonaGate(this MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"""
            DROP POLICY IF EXISTS {table}_persona ON {table};
            DROP POLICY IF EXISTS {table}_persona_no_update ON {table};
            DROP POLICY IF EXISTS {table}_persona_no_delete ON {table};
            """);
    }

    public static void RevokeAppendOnly(this MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"REVOKE UPDATE, DELETE ON {table} FROM leasebook_app, leasebook_ops;");
    }
}
