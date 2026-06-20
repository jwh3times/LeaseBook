using System;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddBankReconciliations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_reconciliations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_year = table.Column<int>(type: "integer", nullable: false),
                    period_month = table.Column<int>(type: "integer", nullable: false),
                    statement_ending_balance = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    finalized_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finalized_by = table.Column<Guid>(type: "uuid", nullable: true),
                    report_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    reopen_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_reconciliations", x => x.id);
                    table.CheckConstraint("ck_bank_reconciliations_status", "status IN ('in_progress','finalized','reopened')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_bank_reconciliations_org_id_bank_account_id_period_year_per",
                table: "bank_reconciliations",
                columns: new[] { "org_id", "bank_account_id", "period_year", "period_month" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_bank_line_status_bank_reconciliation_reconciliation_id",
                table: "bank_line_status",
                column: "reconciliation_id",
                principalTable: "bank_reconciliations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Composite (org_id, bank_account_id) FK to bank_accounts — org-correct, DB-only (P60/ADR-013).
            migrationBuilder.AddForeignKey(
                name: "fk_bank_reconciliations_bank_accounts_org",
                table: "bank_reconciliations",
                columns: new[] { "org_id", "bank_account_id" },
                principalTable: "bank_accounts",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Org-scoped (P70): RLS policy + FORCE. A reconciliation's status mutates (not append-only),
            // so the app role keeps its default INSERT/UPDATE grants.
            migrationBuilder.EnableOrgRls("bank_reconciliations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_bank_line_status_bank_reconciliation_reconciliation_id",
                table: "bank_line_status");

            migrationBuilder.DropTable(
                name: "bank_reconciliations");
        }
    }
}
