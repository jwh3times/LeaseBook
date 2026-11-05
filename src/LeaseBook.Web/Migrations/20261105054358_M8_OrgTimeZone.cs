using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class M8_OrgTimeZone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "time_zone",
                table: "org_settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "America/New_York");

            migrationBuilder.AddCheckConstraint(
                name: "ck_org_settings_time_zone",
                table: "org_settings",
                sql: "time_zone IN ('America/New_York','America/Chicago','America/Denver','America/Phoenix','America/Los_Angeles','America/Anchorage','Pacific/Honolulu')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_org_settings_time_zone",
                table: "org_settings");

            migrationBuilder.DropColumn(
                name: "time_zone",
                table: "org_settings");
        }
    }
}
