using System;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class M8_AddPlatformCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── feature_flags: genuinely global. No org_id — a flag is a property of the deployment,
            //    not of a tenant. This is the ONE table here that belongs in SchemaGuardTests.GlobalTables.
            migrationBuilder.CreateTable(
                name: "feature_flags",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table => table.PrimaryKey("pk_feature_flags", x => x.name));

            //    Tenant-readable, platform-writable. Reads are ungated on purpose: the capability
            //    resolver reads flags inside the ambient REQUEST transaction so a money-path kill
            //    switch takes effect immediately instead of waiting out a cache TTL, and neither way
            //    of getting platform scope in there is safe — PlatformScopedExecutor opens its own
            //    transaction (cannot nest), and SET LOCAL app.platform would persist to end of
            //    transaction and leave the rest of the request with platform scope, defeating org
            //    isolation on entitlements and capability_cohorts. Flags are deployment config, not
            //    tenant data; the property worth protecting is that a tenant cannot TOGGLE a flag.
            //    The helper keeps the app role's full CRUD grant on purpose — the CLI runs as the app
            //    role, and RLS is the write gate here, not the grant. Deliberately NOT append-only:
            //    a flag is mutable state, unlike entitlements.
            migrationBuilder.EnableGlobalReadPlatformWriteRls("feature_flags");

            // ── entitlements: APPEND-ONLY grant events. No revoked_at column — a revoked_at implies UPDATE,
            //    which would keep UPDATE on the table and make RevokeAppendOnly impossible, and leaves
            //    re-grant-after-revoke undefined. Current state = latest row per (org_id, capability).
            migrationBuilder.CreateTable(
                name: "entitlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability = table.Column<string>(type: "text", nullable: false),
                    granted = table.Column<bool>(type: "boolean", nullable: false),
                    effective_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entitlements", x => x.id);
                    table.ForeignKey(
                        name: "fk_entitlements_orgs_org_id",
                        column: x => x.org_id,
                        principalTable: "orgs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_entitlements_org_id_capability_effective_at",
                table: "entitlements",
                columns: ["org_id", "capability", "effective_at"]);

            migrationBuilder.EnableOrgRlsWithPlatformEscape("entitlements");
            migrationBuilder.RevokeAppendOnly("entitlements");

            // ── capability_cohorts: targeting rule. org_id is ALWAYS present; user_id narrows it to one
            //    user within that org. Carrying the pair is deliberate — asp_net_users is itself RLS-exempt,
            //    so a bare user_id could not be validated against its org.
            migrationBuilder.CreateTable(
                name: "capability_cohorts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability = table.Column<string>(type: "text", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    added_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    added_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_capability_cohorts", x => x.id);
                    table.ForeignKey(
                        name: "fk_capability_cohorts_orgs_org_id",
                        column: x => x.org_id,
                        principalTable: "orgs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_capability_cohorts_capability_org_id",
                table: "capability_cohorts",
                columns: ["capability", "org_id"]);

            migrationBuilder.EnableOrgRlsWithPlatformEscape("capability_cohorts");

            // ── platform_audit_events: the existing audit trail structurally cannot hold these rows.
            //    AppDbContext's audit pass filters to IOrgScoped and throws without org context;
            //    AuditEvent.OrgId is non-nullable and audit_events is RLS'd under FORCE.
            //    org_id here is NULLABLE (creating a flag is not org-specific) and is deliberately NOT an
            //    FK: deleting an org must not delete the record of what was done to it.
            migrationBuilder.CreateTable(
                name: "platform_audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    capability = table.Column<string>(type: "text", nullable: true),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detail_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table => table.PrimaryKey("pk_platform_audit_events", x => x.id));

            migrationBuilder.EnablePlatformOnlyRls("platform_audit_events");
            migrationBuilder.RevokeAppendOnly("platform_audit_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Policies, indexes and grants drop with their tables.
            migrationBuilder.DropTable(
                name: "platform_audit_events");

            migrationBuilder.DropTable(
                name: "capability_cohorts");

            migrationBuilder.DropTable(
                name: "entitlements");

            migrationBuilder.DropTable(
                name: "feature_flags");
        }
    }
}
