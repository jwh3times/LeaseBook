using Microsoft.EntityFrameworkCore.Migrations;

namespace LeaseBook.Web.Persistence;

/// <summary>
/// The single helper every org-scoped table's migration uses to get RLS right (CLAUDE.md;
/// §C.3). One call emits ENABLE + FORCE ROW LEVEL SECURITY and the bare-equality org-isolation
/// policy with both USING and WITH CHECK. WP-05's schema guard fails CI if any <c>org_id</c>
/// table is missing this. Append-only tables additionally call <see cref="RevokeAppendOnly"/>.
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
    }

    public static void RevokeAppendOnly(this MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"REVOKE UPDATE, DELETE ON {table} FROM leasebook_app, leasebook_ops;");
    }
}
