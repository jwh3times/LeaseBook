using System;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddStatementDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "statement_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_year = table.Column<int>(type: "integer", nullable: false),
                    period_month = table.Column<int>(type: "integer", nullable: false),
                    to_email = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    artifact_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement_deliveries", x => x.id);
                    table.CheckConstraint("ck_statement_deliveries_state", "state IN ('queued', 'sent', 'failed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_statement_deliveries_org_id_owner_id_period_year_period_mon",
                table: "statement_deliveries",
                columns: new[] { "org_id", "owner_id", "period_year", "period_month" });

            // Org-scoped (§C.3 / CLAUDE.md): RLS policy + FORCE so SchemaGuardTests stays green.
            // statement_deliveries is append-only in intent (delivery records are not updated or
            // deleted by the app role — corrections are new rows); however the M8 Queued→Sent/Failed
            // state transition will UPDATE the row, so we do NOT call RevokeAppendOnly here.
            migrationBuilder.EnableOrgRls("statement_deliveries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "statement_deliveries");
        }
    }
}
