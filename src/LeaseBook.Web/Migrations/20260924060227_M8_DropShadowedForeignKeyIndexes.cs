using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class M8_DropShadowedForeignKeyIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // M8_OrgConstrainCompositeForeignKeys added the three (org_id, foreign_id) indexes below
            // to support the new composite FKs, leaving these earlier single-column indexes shadowed.
            // Runtime reads always receive the org_id predicate from FORCE RLS. On the 300-unit load
            // fixture, the reconciliation composite served 2,841 scans while its single-column
            // predecessor served none; rollback-only plans with 20,000 statement lines and matches
            // kept indexed access through the composites after all three predecessors were dropped.
            migrationBuilder.DropIndex(
                name: "ix_statement_matches_statement_line_id",
                table: "statement_matches");

            migrationBuilder.DropIndex(
                name: "ix_statement_lines_import_id",
                table: "statement_lines");

            migrationBuilder.DropIndex(
                name: "ix_bank_line_status_reconciliation_id",
                table: "bank_line_status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_statement_matches_statement_line_id",
                table: "statement_matches",
                column: "statement_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_statement_lines_import_id",
                table: "statement_lines",
                column: "import_id");

            migrationBuilder.CreateIndex(
                name: "ix_bank_line_status_reconciliation_id",
                table: "bank_line_status",
                column: "reconciliation_id");
        }
    }
}
