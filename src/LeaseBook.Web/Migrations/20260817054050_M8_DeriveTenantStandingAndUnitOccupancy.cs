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

            migrationBuilder.Sql("""
                UPDATE units
                SET availability = 'available'
                WHERE availability IN ('occupied', 'vacant');

                UPDATE tenants
                SET lifecycle_status = 'current'
                WHERE lifecycle_status IN ('late', 'prepaid');
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

            migrationBuilder.Sql("""
                UPDATE units
                SET status = 'vacant'
                WHERE status = 'available';
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
