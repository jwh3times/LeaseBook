using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class CompositeDimensionFks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_units_org_id_id",
                table: "units",
                columns: new[] { "org_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_tenants_org_id_id",
                table: "tenants",
                columns: new[] { "org_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_properties_org_id_id",
                table: "properties",
                columns: new[] { "org_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_owners_org_id_id",
                table: "owners",
                columns: new[] { "org_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_bank_accounts_org_id_id",
                table: "bank_accounts",
                columns: new[] { "org_id", "id" });

            // ADR-013 / P60: promote the five journal_lines dimension FKs from single-column to
            // composite (org_id, <dim>_id) → (org_id, id), so the constraint enforces org-correctness
            // — Postgres RI bypasses RLS, so a single-column FK only proves the id exists in *some*
            // org. DB-level only (no EF navigation properties — P26/ADR-008); restated by hand here.
            // org_id is NOT NULL on journal_lines and the dim columns are nullable, so MATCH SIMPLE
            // skips the check exactly when the dimension is absent. Drop the M2 single-column FKs
            // (names from 20260613043926_AddDirectory) first, then add the composites.
            migrationBuilder.DropForeignKey(name: "fk_journal_lines_owners_owner_id", table: "journal_lines");
            migrationBuilder.DropForeignKey(name: "fk_journal_lines_properties_property_id", table: "journal_lines");
            migrationBuilder.DropForeignKey(name: "fk_journal_lines_units_unit_id", table: "journal_lines");
            migrationBuilder.DropForeignKey(name: "fk_journal_lines_tenants_tenant_id", table: "journal_lines");
            migrationBuilder.DropForeignKey(name: "fk_journal_lines_bank_accounts_bank_account_id", table: "journal_lines");

            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_owners_owner_id", table: "journal_lines",
                columns: new[] { "org_id", "owner_id" },
                principalTable: "owners", principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_properties_property_id", table: "journal_lines",
                columns: new[] { "org_id", "property_id" },
                principalTable: "properties", principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_units_unit_id", table: "journal_lines",
                columns: new[] { "org_id", "unit_id" },
                principalTable: "units", principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_tenants_tenant_id", table: "journal_lines",
                columns: new[] { "org_id", "tenant_id" },
                principalTable: "tenants", principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_bank_accounts_bank_account_id", table: "journal_lines",
                columns: new[] { "org_id", "bank_account_id" },
                principalTable: "bank_accounts", principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse ADR-013: drop the composite FKs and restore the single-column ones BEFORE the
            // (org_id, id) unique constraints they depend on are dropped below.
            migrationBuilder.DropForeignKey(name: "fk_journal_lines_owners_owner_id", table: "journal_lines");
            migrationBuilder.DropForeignKey(name: "fk_journal_lines_properties_property_id", table: "journal_lines");
            migrationBuilder.DropForeignKey(name: "fk_journal_lines_units_unit_id", table: "journal_lines");
            migrationBuilder.DropForeignKey(name: "fk_journal_lines_tenants_tenant_id", table: "journal_lines");
            migrationBuilder.DropForeignKey(name: "fk_journal_lines_bank_accounts_bank_account_id", table: "journal_lines");

            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_owners_owner_id", table: "journal_lines", column: "owner_id",
                principalTable: "owners", principalColumn: "id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_properties_property_id", table: "journal_lines", column: "property_id",
                principalTable: "properties", principalColumn: "id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_units_unit_id", table: "journal_lines", column: "unit_id",
                principalTable: "units", principalColumn: "id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_tenants_tenant_id", table: "journal_lines", column: "tenant_id",
                principalTable: "tenants", principalColumn: "id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_bank_accounts_bank_account_id", table: "journal_lines", column: "bank_account_id",
                principalTable: "bank_accounts", principalColumn: "id", onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropUniqueConstraint(
                name: "ak_units_org_id_id",
                table: "units");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_tenants_org_id_id",
                table: "tenants");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_properties_org_id_id",
                table: "properties");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_owners_org_id_id",
                table: "owners");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_bank_accounts_org_id_id",
                table: "bank_accounts");
        }
    }
}
