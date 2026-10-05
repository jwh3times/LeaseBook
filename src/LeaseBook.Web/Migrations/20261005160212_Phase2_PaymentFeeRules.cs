using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class Phase2_PaymentFeeRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ach_fee_cap",
                table: "org_settings",
                type: "numeric(14,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ach_fee_fixed",
                table: "org_settings",
                type: "numeric(14,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "ach_fee_rate_bps",
                table: "org_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "card_fee_cap",
                table: "org_settings",
                type: "numeric(14,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "card_fee_fixed",
                table: "org_settings",
                type: "numeric(14,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "card_fee_rate_bps",
                table: "org_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_org_settings_ach_fee",
                table: "org_settings",
                sql: "ach_fee_rate_bps >= 0 AND ach_fee_rate_bps < 10000 AND ach_fee_fixed >= 0 AND (ach_fee_cap IS NULL OR ach_fee_cap > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_org_settings_card_fee",
                table: "org_settings",
                sql: "card_fee_rate_bps >= 0 AND card_fee_rate_bps < 10000 AND card_fee_fixed >= 0 AND (card_fee_cap IS NULL OR card_fee_cap > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_org_settings_ach_fee",
                table: "org_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_org_settings_card_fee",
                table: "org_settings");

            migrationBuilder.DropColumn(
                name: "ach_fee_cap",
                table: "org_settings");

            migrationBuilder.DropColumn(
                name: "ach_fee_fixed",
                table: "org_settings");

            migrationBuilder.DropColumn(
                name: "ach_fee_rate_bps",
                table: "org_settings");

            migrationBuilder.DropColumn(
                name: "card_fee_cap",
                table: "org_settings");

            migrationBuilder.DropColumn(
                name: "card_fee_fixed",
                table: "org_settings");

            migrationBuilder.DropColumn(
                name: "card_fee_rate_bps",
                table: "org_settings");
        }
    }
}
