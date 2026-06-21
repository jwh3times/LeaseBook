using System;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddStatementImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_csv_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    column_map_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_csv_mappings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "statement_imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    filename = table.Column<string>(type: "text", nullable: false),
                    imported_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement_imports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "statement_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_date = table.Column<DateOnly>(type: "date", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    dedup_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_statement_lines_statement_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "statement_imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "statement_matches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement_matches", x => x.id);
                    table.CheckConstraint("ck_statement_matches_kind", "kind IN ('matched','suggested','unmatched','created')");
                    table.ForeignKey(
                        name: "fk_statement_matches_statement_lines_statement_line_id",
                        column: x => x.statement_line_id,
                        principalTable: "statement_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bank_csv_mappings_org_id_bank_account_id_name",
                table: "bank_csv_mappings",
                columns: new[] { "org_id", "bank_account_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_statement_imports_org_id_bank_account_id",
                table: "statement_imports",
                columns: new[] { "org_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_statement_lines_import_id",
                table: "statement_lines",
                column: "import_id");

            migrationBuilder.CreateIndex(
                name: "ix_statement_lines_org_id_bank_account_id_dedup_hash",
                table: "statement_lines",
                columns: new[] { "org_id", "bank_account_id", "dedup_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_statement_matches_statement_line_id",
                table: "statement_matches",
                column: "statement_line_id");

            // Composite (org_id, bank_account_id) FKs to bank_accounts — org-correct, DB-only (P60/ADR-013),
            // no EF navigation (P26). statement_matches carries no bank_account_id, so it gets none.
            migrationBuilder.AddForeignKey(
                name: "fk_bank_csv_mappings_bank_accounts_org",
                table: "bank_csv_mappings",
                columns: new[] { "org_id", "bank_account_id" },
                principalTable: "bank_accounts",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_statement_imports_bank_accounts_org",
                table: "statement_imports",
                columns: new[] { "org_id", "bank_account_id" },
                principalTable: "bank_accounts",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_statement_lines_bank_accounts_org",
                table: "statement_lines",
                columns: new[] { "org_id", "bank_account_id" },
                principalTable: "bank_accounts",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Org-scoped (P70): RLS policy + FORCE on each table. None is append-only (imports/lines/matches
            // are operational metadata, mappings are config), so the app role keeps its INSERT/UPDATE grants.
            migrationBuilder.EnableOrgRls("bank_csv_mappings");
            migrationBuilder.EnableOrgRls("statement_imports");
            migrationBuilder.EnableOrgRls("statement_lines");
            migrationBuilder.EnableOrgRls("statement_matches");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_csv_mappings");

            migrationBuilder.DropTable(
                name: "statement_matches");

            migrationBuilder.DropTable(
                name: "statement_lines");

            migrationBuilder.DropTable(
                name: "statement_imports");
        }
    }
}
