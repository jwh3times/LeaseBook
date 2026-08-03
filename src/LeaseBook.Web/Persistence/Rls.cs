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
    /// Org isolation with ONE deliberate escape for platform-plane work (ADR-028). Platform tables
    /// carry org_id because they are data ABOUT orgs, but the maintainer must read across orgs to
    /// operate them. The escape is a GUC set by <c>PlatformScopedExecutor</c> and nowhere else.
    /// <para>
    /// Why an escape rather than no RLS: a path that forgets to open platform scope returns ZERO
    /// rows instead of every org's rows. Visible emptiness beats a silent cross-tenant leak. It also
    /// keeps the table inside SchemaGuardTests' normal org-scoped arm — no new exemption class.
    /// </para>
    /// </summary>
    public static void EnableOrgRlsWithPlatformEscape(this MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"""
            ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
            CREATE POLICY {table}_org_isolation ON {table}
              FOR ALL
              USING (org_id = NULLIF(current_setting('app.org_id', true), '')::uuid
                     OR current_setting('app.platform', true) = 'on')
              WITH CHECK (org_id = NULLIF(current_setting('app.org_id', true), '')::uuid
                     OR current_setting('app.platform', true) = 'on');
            GRANT SELECT, INSERT, UPDATE, DELETE ON {table} TO leasebook_app;
            GRANT SELECT ON {table} TO leasebook_ops;
            """);
    }

    /// <summary>
    /// Platform-plane only: no tenant request can read these rows at all, whatever its org context.
    /// Used for <c>platform_audit_events</c>, whose org_id is nullable (creating a flag is not
    /// org-specific) and which must never be visible inside a tenant session.
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
