using System;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountingCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounting_periods",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    month = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_periods", x => x.id);
                    table.CheckConstraint("ck_accounting_periods_month", "month BETWEEN 1 AND 12");
                    table.CheckConstraint("ck_accounting_periods_status", "status IN ('open','closed')");
                });

            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    @class = table.Column<string>(name: "class", type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.CheckConstraint("ck_accounts_bank_account_id_matches_class", "(bank_account_id IS NOT NULL) = (class IN ('trust_bank','pm_operating_bank'))");
                    table.CheckConstraint("ck_accounts_class", "class IN ('trust_bank','owner_equity','tenant_receivable','deposit_liability','pm_income','pm_operating_bank')");
                });

            migrationBuilder.CreateTable(
                name: "journal_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    event_subtype = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    source_ref = table.Column<string>(type: "text", nullable: true),
                    reverses_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    posted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_journal_entries_journal_entries_reverses_entry_id",
                        column: x => x.reverses_entry_id,
                        principalTable: "journal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "journal_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_class = table.Column<string>(type: "text", nullable: false),
                    debit = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    credit = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    basis = table.Column<string>(type: "text", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: true),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    memo = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_lines", x => x.id);
                    table.CheckConstraint("ck_journal_lines_account_class", "account_class IN ('trust_bank','owner_equity','tenant_receivable','deposit_liability','pm_income','pm_operating_bank')");
                    table.CheckConstraint("ck_journal_lines_basis", "basis IN ('cash','accrual','both')");
                    table.CheckConstraint("ck_journal_lines_debit_xor_credit", "(debit IS NULL) <> (credit IS NULL) AND COALESCE(debit, credit) > 0");
                    table.CheckConstraint("ck_journal_lines_pm_income_no_owner", "account_class <> 'pm_income' OR owner_id IS NULL");
                    table.ForeignKey(
                        name: "fk_journal_lines_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_journal_lines_journal_entries_entry_id",
                        column: x => x.entry_id,
                        principalTable: "journal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_periods_org_id_year_month",
                table: "accounting_periods",
                columns: new[] { "org_id", "year", "month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_accounts_org_id_code",
                table: "accounts",
                columns: new[] { "org_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_org_id_entry_date",
                table: "journal_entries",
                columns: new[] { "org_id", "entry_date" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_org_id_reverses_entry_id",
                table: "journal_entries",
                columns: new[] { "org_id", "reverses_entry_id" },
                unique: true,
                filter: "reverses_entry_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_org_id_source_ref",
                table: "journal_entries",
                columns: new[] { "org_id", "source_ref" },
                unique: true,
                filter: "source_ref IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_reverses_entry_id",
                table: "journal_entries",
                column: "reverses_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_journal_lines_account_id",
                table: "journal_lines",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_journal_lines_entry_id",
                table: "journal_lines",
                column: "entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_journal_lines_org_id_account_id",
                table: "journal_lines",
                columns: new[] { "org_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_lines_org_id_bank_account_id",
                table: "journal_lines",
                columns: new[] { "org_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_lines_org_id_entry_id",
                table: "journal_lines",
                columns: new[] { "org_id", "entry_id" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_lines_org_id_owner_id",
                table: "journal_lines",
                columns: new[] { "org_id", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_lines_org_id_tenant_id",
                table: "journal_lines",
                columns: new[] { "org_id", "tenant_id" });

            // All four tables are org-scoped → RLS (ENABLE + FORCE + org-isolation policy); the schema
            // guard covers them automatically (no allowlist change). journal_entries/journal_lines are
            // additionally append-only — the runtime app/ops roles lose UPDATE/DELETE so a posted row
            // can never be mutated (CLAUDE.md). accounts (rename) and accounting_periods (close) keep
            // UPDATE, so they are not revoked.
            migrationBuilder.EnableOrgRls("accounts");
            migrationBuilder.EnableOrgRls("accounting_periods");
            migrationBuilder.EnableOrgRls("journal_entries");
            migrationBuilder.EnableOrgRls("journal_lines");
            migrationBuilder.RevokeAppendOnly("journal_entries");
            migrationBuilder.RevokeAppendOnly("journal_lines");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounting_periods");

            migrationBuilder.DropTable(
                name: "journal_lines");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "journal_entries");
        }
    }
}
