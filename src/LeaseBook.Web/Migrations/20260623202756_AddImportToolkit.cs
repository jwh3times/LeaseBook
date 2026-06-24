using System;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddImportToolkit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_journal_lines_account_class",
                table: "journal_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_class",
                table: "accounts");

            migrationBuilder.CreateTable(
                name: "import_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_kind = table.Column<string>(type: "text", nullable: false),
                    mapping_profile = table.Column<string>(type: "text", nullable: false),
                    source_filename = table.Column<string>(type: "text", nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    error_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_batches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "migration_verifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cutover_date = table.Column<DateOnly>(type: "date", nullable: false),
                    expected_json = table.Column<string>(type: "jsonb", nullable: false),
                    actual_json = table.Column<string>(type: "jsonb", nullable: false),
                    variance_total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    is_tied = table.Column<bool>(type: "boolean", nullable: false),
                    signed_off_by = table.Column<string>(type: "text", nullable: true),
                    signed_off_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    report_snapshot = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_verifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "import_rows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false),
                    mapped_json = table.Column<string>(type: "jsonb", nullable: false),
                    row_status = table.Column<string>(type: "text", nullable: false),
                    errors_json = table.Column<string>(type: "jsonb", nullable: true),
                    resulting_journal_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_rows", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_rows_import_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_journal_lines_account_class",
                table: "journal_lines",
                sql: "account_class IN ('trust_bank','owner_equity','tenant_receivable','deposit_liability','pm_income','pm_operating_bank','migration_clearing')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_accounts_class",
                table: "accounts",
                sql: "class IN ('trust_bank','owner_equity','tenant_receivable','deposit_liability','pm_income','pm_operating_bank','migration_clearing')");

            // Org-scoped (§C.3 / CLAUDE.md): RLS policy + FORCE so SchemaGuardTests stays green.
            // All three tables are write-once (append-only): the runtime app/ops roles lose UPDATE/DELETE.
            migrationBuilder.EnableOrgRls("import_batches");
            migrationBuilder.RevokeAppendOnly("import_batches");
            migrationBuilder.EnableOrgRls("import_rows");
            migrationBuilder.RevokeAppendOnly("import_rows");
            migrationBuilder.EnableOrgRls("migration_verifications");
            migrationBuilder.RevokeAppendOnly("migration_verifications");

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_org_id_entity_kind_status",
                table: "import_batches",
                columns: new[] { "org_id", "entity_kind", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_import_rows_batch_id",
                table: "import_rows",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_rows_org_id_batch_id",
                table: "import_rows",
                columns: new[] { "org_id", "batch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_verifications_org_id_cutover_date",
                table: "migration_verifications",
                columns: new[] { "org_id", "cutover_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_rows");

            migrationBuilder.DropTable(
                name: "migration_verifications");

            migrationBuilder.DropTable(
                name: "import_batches");

            migrationBuilder.DropCheckConstraint(
                name: "ck_journal_lines_account_class",
                table: "journal_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_class",
                table: "accounts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_journal_lines_account_class",
                table: "journal_lines",
                sql: "account_class IN ('trust_bank','owner_equity','tenant_receivable','deposit_liability','pm_income','pm_operating_bank')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_accounts_class",
                table: "accounts",
                sql: "class IN ('trust_bank','owner_equity','tenant_receivable','deposit_liability','pm_income','pm_operating_bank')");
        }
    }
}
