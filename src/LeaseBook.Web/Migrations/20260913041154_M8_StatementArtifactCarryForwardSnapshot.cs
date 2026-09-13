using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class M8_StatementArtifactCarryForwardSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "as_of",
                table: "statement_artifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ending_balance",
                table: "statement_artifacts",
                type: "numeric(14,2)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "property_id",
                table: "statement_artifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_statement_artifacts_carry_forward_snapshot",
                table: "statement_artifacts",
                sql: "(ending_balance IS NULL) = (as_of IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_statement_artifacts_carry_forward_snapshot",
                table: "statement_artifacts");

            migrationBuilder.DropColumn(
                name: "as_of",
                table: "statement_artifacts");

            migrationBuilder.DropColumn(
                name: "ending_balance",
                table: "statement_artifacts");

            migrationBuilder.DropColumn(
                name: "property_id",
                table: "statement_artifacts");
        }
    }
}
