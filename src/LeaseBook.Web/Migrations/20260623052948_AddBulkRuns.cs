using System;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddBulkRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bulk_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_type = table.Column<string>(type: "text", nullable: false),
                    period_year = table.Column<int>(type: "integer", nullable: false),
                    period_month = table.Column<int>(type: "integer", nullable: false),
                    summary_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bulk_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bulk_run_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bulk_run_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_bulk_run_items_bulk_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "bulk_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bulk_run_items_org_id_run_id",
                table: "bulk_run_items",
                columns: new[] { "org_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_bulk_run_items_run_id",
                table: "bulk_run_items",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_bulk_runs_org_id_run_type_period_year_period_month",
                table: "bulk_runs",
                columns: new[] { "org_id", "run_type", "period_year", "period_month" });

            // Org-scoped (§C.3 / CLAUDE.md): RLS policy + FORCE so SchemaGuardTests stays green.
            // bulk_runs and bulk_run_items are append-only in intent (run history is never corrected);
            // however, the summary_json on bulk_runs is written once at confirm time so no UPDATE is
            // needed after the initial insert. We do NOT call RevokeAppendOnly here because both tables
            // are stamped by the same SaveChangesAsync call (no post-save update is required).
            migrationBuilder.EnableOrgRls("bulk_runs");
            migrationBuilder.EnableOrgRls("bulk_run_items");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bulk_run_items");

            migrationBuilder.DropTable(
                name: "bulk_runs");
        }
    }
}
