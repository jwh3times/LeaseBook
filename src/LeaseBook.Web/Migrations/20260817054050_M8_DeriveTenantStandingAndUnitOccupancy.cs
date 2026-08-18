using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class M8_DeriveTenantStandingAndUnitOccupancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_units_status",
                table: "units");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_status",
                table: "tenants");

            migrationBuilder.RenameColumn(
                name: "status",
                table: "units",
                newName: "availability");

            migrationBuilder.RenameColumn(
                name: "status",
                table: "tenants",
                newName: "lifecycle_status");

            // FORCE ROW LEVEL SECURITY binds the schema owner too, so this backfill runs as
            // leasebook_migrator *subject to* each table's org-isolation policy. A migration
            // transaction sets no app.org_id, so the policy predicate is NULL, the UPDATEs silently
            // match zero rows, and AddCheckConstraint below then fails (23514) on the very values
            // the backfill was supposed to rewrite. Lifting FORCE is the only way a migration can
            // rewrite every org's rows at once; it is restored in the same transaction, and DDL is
            // transactional in Postgres, so any failure rolls back with FORCE intact.
            migrationBuilder.Sql("""
                ALTER TABLE units NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE tenants NO FORCE ROW LEVEL SECURITY;

                UPDATE units
                SET availability = 'available'
                WHERE availability IN ('occupied', 'vacant');

                UPDATE tenants
                SET lifecycle_status = 'current'
                WHERE lifecycle_status IN ('late', 'prepaid');

                ALTER TABLE units FORCE ROW LEVEL SECURITY;
                ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_units_availability",
                table: "units",
                sql: "availability IN ('available','unavailable')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_lifecycle_status",
                table: "tenants",
                sql: "lifecycle_status IN ('current','evicting','past')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_units_availability",
                table: "units");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_lifecycle_status",
                table: "tenants");

            migrationBuilder.RenameColumn(
                name: "availability",
                table: "units",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "lifecycle_status",
                table: "tenants",
                newName: "status");

            // Same reason as Up: FORCE RLS filters the migrator, so the backfill must run with it
            // lifted or it rewrites nothing and ck_units_status below rejects the leftovers.
            migrationBuilder.Sql("""
                ALTER TABLE units NO FORCE ROW LEVEL SECURITY;

                UPDATE units
                SET status = 'vacant'
                WHERE status = 'available';

                ALTER TABLE units FORCE ROW LEVEL SECURITY;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_units_status",
                table: "units",
                sql: "status IN ('occupied','vacant','unavailable')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_status",
                table: "tenants",
                sql: "status IN ('current','late','prepaid','evicting','past')");
        }
    }
}
