using System;
using Microsoft.EntityFrameworkCore.Migrations;
using LeaseBook.Web.Persistence;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class P2_SimulatedPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_fixtures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_fixtures", x => x.id);
                    table.UniqueConstraint("ak_payment_fixtures_org_id_id", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "fk_payment_fixtures_bank_accounts_org_id_bank_id",
                        columns: x => new { x.org_id, x.bank_id },
                        principalTable: "bank_accounts",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_observations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    provider_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    generation = table.Column<Guid>(type: "uuid", nullable: false),
                    account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    gross = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    fee = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    net = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    bank_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_date = table.Column<DateOnly>(type: "date", nullable: false),
                    evidence_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payout_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    complete = table.Column<bool>(type: "boolean", nullable: false),
                    observed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_observations", x => x.id);
                    table.UniqueConstraint("ak_payment_observations_org_id_id", x => new { x.org_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "simulated_collections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_simulated_collections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payment_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    provider_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    processed_count = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    due_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    lease_claim_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_operations", x => x.id);
                    table.UniqueConstraint("ak_payment_operations_org_id_id", x => new { x.org_id, x.id });
                    table.CheckConstraint("ck_payment_operation_amount", "amount > 0 AND amount <= 10000");
                    table.CheckConstraint("ck_payment_operation_currency", "currency = 'USD'");
                    table.CheckConstraint("ck_payment_operation_status", "status IN ('Requested','Processing','Failed','Settled','NeedsReview')");
                    table.ForeignKey(
                        name: "fk_payment_operations_asp_net_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "asp_net_users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_operations_bank_accounts_org_id_bank_id",
                        columns: x => new { x.org_id, x.bank_id },
                        principalTable: "bank_accounts",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_operations_journal_entries_org_id_journal_id",
                        columns: x => new { x.org_id, x.journal_id },
                        principalTable: "journal_entries",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_operations_payment_fixtures_org_id_generation",
                        columns: x => new { x.org_id, x.generation },
                        principalTable: "payment_fixtures",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_operations_tenants_org_id_tenant_id",
                        columns: x => new { x.org_id, x.tenant_id },
                        principalTable: "tenants",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_effects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_effects", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_effects_journal_entries_org_id_journal_id",
                        columns: x => new { x.org_id, x.journal_id },
                        principalTable: "journal_entries",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_effects_payment_observation_org_id_observation_id",
                        columns: x => new { x.org_id, x.observation_id },
                        principalTable: "payment_observations",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_effects_payment_operation_org_id_operation_id",
                        columns: x => new { x.org_id, x.operation_id },
                        principalTable: "payment_operations",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_effects_org_id_journal_id",
                table: "payment_effects",
                columns: new[] { "org_id", "journal_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_effects_org_id_observation_id",
                table: "payment_effects",
                columns: new[] { "org_id", "observation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_effects_org_id_operation_id_kind",
                table: "payment_effects",
                columns: new[] { "org_id", "operation_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_fixtures_org_id",
                table: "payment_fixtures",
                column: "org_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_fixtures_org_id_bank_id",
                table: "payment_fixtures",
                columns: new[] { "org_id", "bank_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_observations_org_id_account_mode_event_id",
                table: "payment_observations",
                columns: new[] { "org_id", "account", "mode", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_observations_org_id_provider_id",
                table: "payment_observations",
                columns: new[] { "org_id", "provider_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_operations_org_id_account_provider_id",
                table: "payment_operations",
                columns: new[] { "org_id", "account", "provider_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_operations_org_id_bank_id",
                table: "payment_operations",
                columns: new[] { "org_id", "bank_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_operations_org_id_due_at",
                table: "payment_operations",
                columns: new[] { "org_id", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_operations_org_id_generation",
                table: "payment_operations",
                columns: new[] { "org_id", "generation" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_operations_org_id_journal_id",
                table: "payment_operations",
                columns: new[] { "org_id", "journal_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_operations_org_id_tenant_id_created_at",
                table: "payment_operations",
                columns: new[] { "org_id", "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_operations_org_id_user_id_key",
                table: "payment_operations",
                columns: new[] { "org_id", "user_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_simulated_collections_org_id_provider_id",
                table: "simulated_collections",
                columns: new[] { "org_id", "provider_id" },
                unique: true);

            migrationBuilder.EnableOrgRls("payment_fixtures");
            migrationBuilder.EnableOrgRls("payment_operations");
            migrationBuilder.EnableOrgRls("payment_observations");
            migrationBuilder.EnableOrgRls("payment_effects");
            migrationBuilder.EnableOrgRls("simulated_collections");
            migrationBuilder.RevokeAppendOnly("payment_fixtures");
            migrationBuilder.RevokeAppendOnly("payment_observations");
            migrationBuilder.RevokeAppendOnly("payment_effects");
            migrationBuilder.RevokeAppendOnly("simulated_collections");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_effects");

            migrationBuilder.DropTable(
                name: "simulated_collections");

            migrationBuilder.DropTable(
                name: "payment_observations");

            migrationBuilder.DropTable(
                name: "payment_operations");

            migrationBuilder.DropTable(
                name: "payment_fixtures");

        }
    }
}
