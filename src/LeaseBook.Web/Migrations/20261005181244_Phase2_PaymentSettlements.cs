using System;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class Phase2_PaymentSettlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "observation_id",
                table: "payment_effects",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "settlement_id",
                table: "payment_effects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "payment_settlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<Guid>(type: "uuid", nullable: false),
                    account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payout_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payout_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    bank_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_date = table.Column<DateOnly>(type: "date", nullable: false),
                    bank_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    evidence_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    reason_item = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    posted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    posted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    review_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    review_closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    review_closed_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_settlements", x => x.id);
                    table.UniqueConstraint("ak_payment_settlements_org_id_id", x => new { x.org_id, x.id });
                    table.CheckConstraint("ck_payment_settlement_status", "status IN ('Received','NeedsReview','Posted','Closed')");
                    table.ForeignKey(
                        name: "fk_payment_settlements_payment_fixtures_org_id_generation",
                        columns: x => new { x.org_id, x.generation },
                        principalTable: "payment_fixtures",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_settlement_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    settlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    gross = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    fee = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    net = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fee_entry_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_settlement_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_settlement_items_payment_settlements_org_id_settlem",
                        columns: x => new { x.org_id, x.settlement_id },
                        principalTable: "payment_settlements",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_effects_org_id_settlement_id",
                table: "payment_effects",
                columns: new[] { "org_id", "settlement_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_effect_evidence",
                table: "payment_effects",
                sql: "(observation_id IS NULL) <> (settlement_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_payment_settlement_items_org_id_provider_id",
                table: "payment_settlement_items",
                columns: new[] { "org_id", "provider_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_settlement_items_org_id_settlement_id_item",
                table: "payment_settlement_items",
                columns: new[] { "org_id", "settlement_id", "item" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_settlements_org_id_account_payout_id",
                table: "payment_settlements",
                columns: new[] { "org_id", "account", "payout_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_settlements_org_id_created_at",
                table: "payment_settlements",
                columns: new[] { "org_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_settlements_org_id_generation",
                table: "payment_settlements",
                columns: new[] { "org_id", "generation" });

            migrationBuilder.AddForeignKey(
                name: "fk_payment_effects_payment_settlement_org_id_settlement_id",
                table: "payment_effects",
                columns: new[] { "org_id", "settlement_id" },
                principalTable: "payment_settlements",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Organization isolation and the deny-by-default persona gate (ADR-048). A payout is staff
            // and system work: no tenant or owner grant is written, so neither persona can read one.
            migrationBuilder.EnableOrgRls("payment_settlements");
            migrationBuilder.EnableOrgRls("payment_settlement_items");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_payment_effects_payment_settlement_org_id_settlement_id",
                table: "payment_effects");

            migrationBuilder.DropTable(
                name: "payment_settlement_items");

            migrationBuilder.DropTable(
                name: "payment_settlements");

            migrationBuilder.DropIndex(
                name: "ix_payment_effects_org_id_settlement_id",
                table: "payment_effects");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_effect_evidence",
                table: "payment_effects");

            migrationBuilder.DropColumn(
                name: "settlement_id",
                table: "payment_effects");

            migrationBuilder.AlterColumn<Guid>(
                name: "observation_id",
                table: "payment_effects",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
