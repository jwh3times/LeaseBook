using System;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // pg_trgm powers the fuzzy ⌘K search (P43/ADR-009). Created first (migrator role owns it),
            // before any gin_trgm_ops index below references it. Ships with the postgres:18 image.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.CreateTable(
                name: "bank_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    institution = table.Column<string>(type: "text", nullable: true),
                    mask = table.Column<string>(type: "text", nullable: true),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_accounts", x => x.id);
                    table.CheckConstraint("ck_bank_accounts_purpose", "purpose IN ('trust','deposit','operating')");
                });

            migrationBuilder.CreateTable(
                name: "org_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accounting_basis = table.Column<string>(type: "text", nullable: false, defaultValue: "cash"),
                    money_negative_display = table.Column<string>(type: "text", nullable: false, defaultValue: "minus"),
                    legal_name = table.Column<string>(type: "text", nullable: true),
                    address = table.Column<string>(type: "text", nullable: true),
                    city = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: true),
                    zip = table.Column<string>(type: "text", nullable: true),
                    phone = table.Column<string>(type: "text", nullable: true),
                    logo_blob_ref = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_org_settings", x => x.id);
                    table.CheckConstraint("ck_org_settings_accounting_basis", "accounting_basis IN ('cash','accrual')");
                    table.CheckConstraint("ck_org_settings_money_negative_display", "money_negative_display IN ('minus','parens')");
                });

            migrationBuilder.CreateTable(
                name: "owners",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    initials = table.Column<string>(type: "text", nullable: true),
                    contact_email = table.Column<string>(type: "text", nullable: true),
                    contact_phone = table.Column<string>(type: "text", nullable: true),
                    default_mgmt_fee_bps = table.Column<int>(type: "integer", nullable: true),
                    reserve_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false, defaultValueSql: "0"),
                    is_system = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_owners", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    contact_email = table.Column<string>(type: "text", nullable: true),
                    contact_phone = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                    table.CheckConstraint("ck_tenants_status", "status IN ('current','late','prepaid','evicting','past')");
                });

            migrationBuilder.CreateTable(
                name: "properties",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "text", nullable: false),
                    city = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: true),
                    zip = table.Column<string>(type: "text", nullable: true),
                    mgmt_fee_bps = table.Column<int>(type: "integer", nullable: true),
                    is_system = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_properties", x => x.id);
                    table.ForeignKey(
                        name: "fk_properties_owners_owner_id",
                        column: x => x.owner_id,
                        principalTable: "owners",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "units",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    rent = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false, defaultValueSql: "0"),
                    status = table.Column<string>(type: "text", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_units", x => x.id);
                    table.CheckConstraint("ck_units_status", "status IN ('occupied','vacant','unavailable')");
                    table.ForeignKey(
                        name: "fk_units_properties_property_id",
                        column: x => x.property_id,
                        principalTable: "properties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lease_lite",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    rent = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    deposit_required = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false, defaultValueSql: "0"),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lease_lite", x => x.id);
                    table.CheckConstraint("ck_lease_lite_status", "status IN ('active','ended','pending')");
                    table.ForeignKey(
                        name: "fk_lease_lite_tenant_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lease_lite_unit_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bank_accounts_org_id_name",
                table: "bank_accounts",
                columns: new[] { "org_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_lease_lite_org_id_tenant_id",
                table: "lease_lite",
                columns: new[] { "org_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lease_lite_org_id_unit_id",
                table: "lease_lite",
                columns: new[] { "org_id", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lease_lite_tenant_id",
                table: "lease_lite",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_lease_lite_unit_id",
                table: "lease_lite",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_org_settings_org_id",
                table: "org_settings",
                column: "org_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_owners_org_id_name",
                table: "owners",
                columns: new[] { "org_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_properties_org_id_address",
                table: "properties",
                columns: new[] { "org_id", "address" });

            migrationBuilder.CreateIndex(
                name: "ix_properties_org_id_owner_id",
                table: "properties",
                columns: new[] { "org_id", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_properties_owner_id",
                table: "properties",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_org_id_display_name",
                table: "tenants",
                columns: new[] { "org_id", "display_name" });

            migrationBuilder.CreateIndex(
                name: "ix_units_org_id_property_id",
                table: "units",
                columns: new[] { "org_id", "property_id" });

            migrationBuilder.CreateIndex(
                name: "ix_units_property_id",
                table: "units",
                column: "property_id");

            // GIN trigram indexes for fuzzy search (§C.1/§C.5, P43). Not expressible through the EF
            // index model (gin_trgm_ops operator class), so authored here as raw SQL. They drop with
            // their tables in Down(); the schema guard does not cover them, so this list is the guard.
            migrationBuilder.Sql("CREATE INDEX ix_owners_name_trgm ON owners USING gin (name gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_properties_address_trgm ON properties USING gin (address gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_units_label_trgm ON units USING gin (label gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_tenants_display_name_trgm ON tenants USING gin (display_name gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_bank_accounts_name_trgm ON bank_accounts USING gin (name gin_trgm_ops);");

            // P38: bind the journal-dimension columns to the directory rows they have always carried
            // (P26). DB-level constraints only — the Accounting entities gain no navigation properties,
            // so the modules stay decoupled (consistent with P39). Nullable FKs (the columns already
            // allow NULL), ON DELETE RESTRICT. These validate trivially here because a fresh DB migrates
            // before it seeds — journal_lines is empty at this point (M2-E1); the seeder then materializes
            // every directory row (incl. the is_system aggregates, §C.2) before replaying the journal, so
            // no journal row ever changes and the M1 golden figures stay byte-identical (ADR-008).
            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_owners_owner_id",
                table: "journal_lines", column: "owner_id",
                principalTable: "owners", principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_properties_property_id",
                table: "journal_lines", column: "property_id",
                principalTable: "properties", principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_units_unit_id",
                table: "journal_lines", column: "unit_id",
                principalTable: "units", principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_tenants_tenant_id",
                table: "journal_lines", column: "tenant_id",
                principalTable: "tenants", principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_bank_accounts_bank_account_id",
                table: "journal_lines", column: "bank_account_id",
                principalTable: "bank_accounts", principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Every directory table is org-scoped → RLS is the boundary (ENABLE + FORCE + isolation
            // policy). The schema guard covers all seven automatically (no allowlist change). These are
            // CRUD aggregates, not the append-only journal, so they keep UPDATE/DELETE (no RevokeAppendOnly).
            migrationBuilder.EnableOrgRls("owners");
            migrationBuilder.EnableOrgRls("properties");
            migrationBuilder.EnableOrgRls("units");
            migrationBuilder.EnableOrgRls("tenants");
            migrationBuilder.EnableOrgRls("lease_lite");
            migrationBuilder.EnableOrgRls("bank_accounts");
            migrationBuilder.EnableOrgRls("org_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop the cross-module FKs first — journal_lines (Accounting's table) is not dropped here,
            // so it would otherwise pin the directory tables it references.
            migrationBuilder.DropForeignKey("fk_journal_lines_owners_owner_id", "journal_lines");
            migrationBuilder.DropForeignKey("fk_journal_lines_properties_property_id", "journal_lines");
            migrationBuilder.DropForeignKey("fk_journal_lines_units_unit_id", "journal_lines");
            migrationBuilder.DropForeignKey("fk_journal_lines_tenants_tenant_id", "journal_lines");
            migrationBuilder.DropForeignKey("fk_journal_lines_bank_accounts_bank_account_id", "journal_lines");

            // GIN indexes drop with their tables. pg_trgm is left installed (harmless; other features
            // may rely on it). RLS policies drop with their tables.
            migrationBuilder.DropTable(
                name: "bank_accounts");

            migrationBuilder.DropTable(
                name: "lease_lite");

            migrationBuilder.DropTable(
                name: "org_settings");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropTable(
                name: "units");

            migrationBuilder.DropTable(
                name: "properties");

            migrationBuilder.DropTable(
                name: "owners");
        }
    }
}
