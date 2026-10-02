using System;
using Microsoft.EntityFrameworkCore.Migrations;
using LeaseBook.Web.Persistence;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class Phase2_RefundChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "check_print_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offset_x_points = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    offset_y_points = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_check_print_settings", x => x.id);
                    table.CheckConstraint("ck_check_print_settings_offset_x", "offset_x_points BETWEEN -72 AND 72");
                    table.CheckConstraint("ck_check_print_settings_offset_y", "offset_y_points BETWEEN -72 AND 72");
                });

            migrationBuilder.CreateTable(
                name: "refund_checks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    check_number = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    payee_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    address_line1 = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    address_line2 = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    city = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    state = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    postal_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    memo = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund_checks", x => x.id);
                    table.UniqueConstraint("ak_refund_checks_org_id_id", x => new { x.org_id, x.id });
                    table.CheckConstraint("ck_refund_checks_amount", "amount > 0");
                    table.CheckConstraint("ck_refund_checks_check_number", "check_number > 0");
                    table.CheckConstraint("ck_refund_checks_source", "source IN ('deposit','prepayment')");
                });

            migrationBuilder.CreateTable(
                name: "refund_check_prints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund_check_prints", x => x.id);
                    table.ForeignKey(
                        name: "fk_refund_check_prints_refund_checks_org_id_check_id",
                        columns: x => new { x.org_id, x.check_id },
                        principalTable: "refund_checks",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_check_print_settings_org_id_bank_account_id",
                table: "check_print_settings",
                columns: new[] { "org_id", "bank_account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refund_check_prints_org_id_check_id",
                table: "refund_check_prints",
                columns: new[] { "org_id", "check_id" });

            migrationBuilder.CreateIndex(
                name: "ix_refund_checks_org_id_bank_account_id_check_number",
                table: "refund_checks",
                columns: new[] { "org_id", "bank_account_id", "check_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refund_checks_org_id_entry_id",
                table: "refund_checks",
                columns: new[] { "org_id", "entry_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refund_checks_org_id_key",
                table: "refund_checks",
                columns: new[] { "org_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refund_checks_org_id_tenant_id",
                table: "refund_checks",
                columns: new[] { "org_id", "tenant_id" });

            // Staff/system only: no portal grant, so tenants and owners see none of these rows (ADR-048).
            migrationBuilder.EnableOrgRls("refund_checks");
            migrationBuilder.EnableOrgRls("refund_check_prints");
            migrationBuilder.EnableOrgRls("check_print_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "check_print_settings");

            migrationBuilder.DropTable(
                name: "refund_check_prints");

            migrationBuilder.DropTable(
                name: "refund_checks");
        }
    }
}
