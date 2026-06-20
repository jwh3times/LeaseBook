using System;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddBankLineStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_line_status",
                columns: table => new
                {
                    journal_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    cleared_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reconciliation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_line_status", x => x.journal_line_id);
                    table.CheckConstraint("ck_bank_line_status_status", "status IN ('uncleared','cleared','reconciled')");
                    table.ForeignKey(
                        name: "fk_bank_line_status_journal_line_journal_line_id",
                        column: x => x.journal_line_id,
                        principalTable: "journal_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bank_line_status_org_id_status",
                table: "bank_line_status",
                columns: new[] { "org_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_bank_line_status_reconciliation_id",
                table: "bank_line_status",
                column: "reconciliation_id");

            // Org-scoped (P70): RLS policy + FORCE so the schema guard passes and the table is isolated.
            // NOT append-only — clearance status mutates (P62) — so the app role keeps its default
            // INSERT/UPDATE/DELETE grants (no RevokeAppendOnly).
            migrationBuilder.EnableOrgRls("bank_line_status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_line_status");
        }
    }
}
