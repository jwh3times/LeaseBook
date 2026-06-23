using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLateFeePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "late_fee_amount",
                table: "org_settings",
                type: "numeric(14,2)",
                nullable: false,
                defaultValue: 50m);

            migrationBuilder.AddColumn<int>(
                name: "late_fee_grace_days",
                table: "org_settings",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<string>(
                name: "late_fee_kind",
                table: "org_settings",
                type: "text",
                nullable: false,
                defaultValue: "flat");

            migrationBuilder.AddColumn<int>(
                name: "late_fee_rate_bps",
                table: "org_settings",
                type: "integer",
                nullable: false,
                defaultValue: 500);

            migrationBuilder.AddColumn<int>(
                name: "rent_due_day",
                table: "org_settings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // Check constraint: late_fee_kind must be one of the known values.
            migrationBuilder.AddCheckConstraint(
                name: "ck_org_settings_late_fee_kind",
                table: "org_settings",
                sql: "late_fee_kind IN ('flat', 'percent')");

            migrationBuilder.AddColumn<decimal>(
                name: "late_fee_amount_override",
                table: "lease_lite",
                type: "numeric(14,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "late_fee_grace_days_override",
                table: "lease_lite",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "late_fee_kind_override",
                table: "lease_lite",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "late_fee_rate_bps_override",
                table: "lease_lite",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "late_fee_rent_due_day_override",
                table: "lease_lite",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_org_settings_late_fee_kind",
                table: "org_settings");

            migrationBuilder.DropColumn(
                name: "late_fee_amount",
                table: "org_settings");

            migrationBuilder.DropColumn(
                name: "late_fee_grace_days",
                table: "org_settings");

            migrationBuilder.DropColumn(
                name: "late_fee_kind",
                table: "org_settings");

            migrationBuilder.DropColumn(
                name: "late_fee_rate_bps",
                table: "org_settings");

            migrationBuilder.DropColumn(
                name: "rent_due_day",
                table: "org_settings");

            migrationBuilder.DropColumn(
                name: "late_fee_amount_override",
                table: "lease_lite");

            migrationBuilder.DropColumn(
                name: "late_fee_grace_days_override",
                table: "lease_lite");

            migrationBuilder.DropColumn(
                name: "late_fee_kind_override",
                table: "lease_lite");

            migrationBuilder.DropColumn(
                name: "late_fee_rate_bps_override",
                table: "lease_lite");

            migrationBuilder.DropColumn(
                name: "late_fee_rent_due_day_override",
                table: "lease_lite");
        }
    }
}
