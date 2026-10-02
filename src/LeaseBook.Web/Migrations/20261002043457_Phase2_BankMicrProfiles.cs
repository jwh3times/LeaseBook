using System;
using Microsoft.EntityFrameworkCore.Migrations;
using LeaseBook.Web.Persistence;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class Phase2_BankMicrProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_micr_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    routing_number = table.Column<string>(type: "text", nullable: true),
                    on_us_account_number = table.Column<string>(type: "text", nullable: true),
                    micr_offset_x_points = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    micr_offset_y_points = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_micr_profiles", x => x.id);
                    table.CheckConstraint("ck_bank_micr_profiles_micr_offset_x", "micr_offset_x_points BETWEEN -18 AND 18");
                    table.CheckConstraint("ck_bank_micr_profiles_micr_offset_y", "micr_offset_y_points BETWEEN -18 AND 18");
                    table.CheckConstraint("ck_bank_micr_profiles_stock_kind", "stock_kind IN ('preprinted', 'blank')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_bank_micr_profiles_org_id_bank_account_id",
                table: "bank_micr_profiles",
                columns: new[] { "org_id", "bank_account_id" },
                unique: true);

            // #474: org isolation plus the deny-by-default persona gate; no portal persona reads bank numbers.
            migrationBuilder.EnableOrgRls("bank_micr_profiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_micr_profiles");
        }
    }
}
