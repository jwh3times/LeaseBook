using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class Phase2_PayoutsHeldForBank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A payout a processor reports apart from any bank (ADR-054) is stored and held. No table is
            // added and no row is rewritten: three columns, one more status, the payout's mode in the
            // key it is found by, and a constraint that a posted payout holds bank evidence. Every
            // payout stored before this was the simulator's, which the column default says, and none
            // of those was stored without an evidence reference. The default is DDL, so no
            // row-level-security bracket is needed.
            migrationBuilder.DropIndex(
                name: "ix_payment_settlements_org_id_account_payout_id",
                table: "payment_settlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_settlement_status",
                table: "payment_settlements");

            migrationBuilder.AddColumn<DateTime>(
                name: "failed_at",
                table: "payment_settlements",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "payment_settlements",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Simulation");

            migrationBuilder.AddColumn<decimal>(
                name: "processor_amount",
                table: "payment_settlements",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_settlements_org_id_account_mode_payout_id",
                table: "payment_settlements",
                columns: new[] { "org_id", "account", "mode", "payout_id" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_settlement_posted_banked",
                table: "payment_settlements",
                sql: "status <> 'Posted' OR evidence_id <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_settlement_status",
                table: "payment_settlements",
                sql: "status IN ('Received','AwaitingBank','NeedsReview','Posted','Closed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payment_settlements_org_id_account_mode_payout_id",
                table: "payment_settlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_settlement_posted_banked",
                table: "payment_settlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_settlement_status",
                table: "payment_settlements");

            migrationBuilder.DropColumn(
                name: "failed_at",
                table: "payment_settlements");

            migrationBuilder.DropColumn(
                name: "mode",
                table: "payment_settlements");

            migrationBuilder.DropColumn(
                name: "processor_amount",
                table: "payment_settlements");

            migrationBuilder.CreateIndex(
                name: "ix_payment_settlements_org_id_account_payout_id",
                table: "payment_settlements",
                columns: new[] { "org_id", "account", "payout_id" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_settlement_status",
                table: "payment_settlements",
                sql: "status IN ('Received','NeedsReview','Posted','Closed')");
        }
    }
}
