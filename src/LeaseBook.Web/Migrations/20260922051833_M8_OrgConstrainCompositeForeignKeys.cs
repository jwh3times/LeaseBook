using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <summary>
    /// Re-points every foreign key whose referencing AND referenced table are both org-scoped onto the
    /// composite <c>(org_id, id)</c> shape, and gives the seven principals that lacked one an
    /// <c>(org_id, id)</c> alternate key to be the target.
    /// <para>
    /// Referential integrity is checked by the system with row-level security bypassed, so a
    /// single-column key only ever proved the referenced row existed in SOME organization — not in the
    /// referencing row's own. RLS cannot close that gap, because a policy only ever sees one table.
    /// A row written with another organization's id was therefore accepted by Postgres and only
    /// surfaced later, as a reference that reads as missing under its own organization's context.
    /// </para>
    /// <para>
    /// Constraint shape only: no column is added, dropped, retyped or rewritten, and there is no DML,
    /// so no FORCE-RLS bracketing is needed. The dropped single-column indexes are the ones EF created
    /// to back the old keys; an explicitly configured <c>(org_id, …)</c> index already covers each one.
    /// Nullable references (<c>reverses_entry_id</c>, <c>supersedes_batch_id</c>,
    /// <c>reconciliation_id</c>) stay optional — <c>org_id</c> is never null, and MATCH SIMPLE skips
    /// the check entirely while the other column is.
    /// </para>
    /// <para>
    /// <c>SchemaGuardTests.Every_foreign_key_between_org_scoped_tables_is_org_constrained</c> walks the
    /// live catalog and keeps this true for tables that do not exist yet.
    /// </para>
    /// </summary>
    public partial class M8_OrgConstrainCompositeForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_bank_line_status_bank_reconciliation_reconciliation_id",
                table: "bank_line_status");

            migrationBuilder.DropForeignKey(
                name: "fk_bank_line_status_journal_line_journal_line_id",
                table: "bank_line_status");

            migrationBuilder.DropForeignKey(
                name: "fk_bulk_run_items_bulk_runs_run_id",
                table: "bulk_run_items");

            migrationBuilder.DropForeignKey(
                name: "fk_import_batches_import_batches_supersedes_batch_id",
                table: "import_batches");

            migrationBuilder.DropForeignKey(
                name: "fk_import_rows_import_batches_batch_id",
                table: "import_rows");

            migrationBuilder.DropForeignKey(
                name: "fk_journal_entries_journal_entries_reverses_entry_id",
                table: "journal_entries");

            migrationBuilder.DropForeignKey(
                name: "fk_journal_lines_accounts_account_id",
                table: "journal_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_journal_lines_journal_entries_entry_id",
                table: "journal_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_lease_lite_tenant_tenant_id",
                table: "lease_lite");

            migrationBuilder.DropForeignKey(
                name: "fk_lease_lite_unit_unit_id",
                table: "lease_lite");

            migrationBuilder.DropForeignKey(
                name: "fk_properties_owners_owner_id",
                table: "properties");

            migrationBuilder.DropForeignKey(
                name: "fk_statement_lines_statement_imports_import_id",
                table: "statement_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_statement_matches_statement_lines_statement_line_id",
                table: "statement_matches");

            migrationBuilder.DropForeignKey(
                name: "fk_units_properties_property_id",
                table: "units");

            migrationBuilder.DropIndex(
                name: "ix_units_property_id",
                table: "units");

            migrationBuilder.DropIndex(
                name: "ix_properties_owner_id",
                table: "properties");

            migrationBuilder.DropIndex(
                name: "ix_lease_lite_tenant_id",
                table: "lease_lite");

            migrationBuilder.DropIndex(
                name: "ix_lease_lite_unit_id",
                table: "lease_lite");

            migrationBuilder.DropIndex(
                name: "ix_journal_lines_account_id",
                table: "journal_lines");

            migrationBuilder.DropIndex(
                name: "ix_journal_lines_entry_id",
                table: "journal_lines");

            migrationBuilder.DropIndex(
                name: "ix_journal_entries_reverses_entry_id",
                table: "journal_entries");

            migrationBuilder.DropIndex(
                name: "ix_import_rows_batch_id",
                table: "import_rows");

            migrationBuilder.DropIndex(
                name: "ix_import_batches_supersedes_batch_id",
                table: "import_batches");

            migrationBuilder.DropIndex(
                name: "ix_bulk_run_items_run_id",
                table: "bulk_run_items");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_statement_lines_org_id_id",
                table: "statement_lines",
                columns: new[] { "org_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_statement_imports_org_id_id",
                table: "statement_imports",
                columns: new[] { "org_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_journal_lines_org_id_id",
                table: "journal_lines",
                columns: new[] { "org_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_import_batches_org_id_id",
                table: "import_batches",
                columns: new[] { "org_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_bulk_runs_org_id_id",
                table: "bulk_runs",
                columns: new[] { "org_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_bank_reconciliations_org_id_id",
                table: "bank_reconciliations",
                columns: new[] { "org_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_accounts_org_id_id",
                table: "accounts",
                columns: new[] { "org_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_statement_matches_org_id_statement_line_id",
                table: "statement_matches",
                columns: new[] { "org_id", "statement_line_id" });

            migrationBuilder.CreateIndex(
                name: "ix_statement_lines_org_id_import_id",
                table: "statement_lines",
                columns: new[] { "org_id", "import_id" });

            migrationBuilder.CreateIndex(
                name: "ix_bank_line_status_org_id_journal_line_id",
                table: "bank_line_status",
                columns: new[] { "org_id", "journal_line_id" });

            migrationBuilder.CreateIndex(
                name: "ix_bank_line_status_org_id_reconciliation_id",
                table: "bank_line_status",
                columns: new[] { "org_id", "reconciliation_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_bank_line_status_journal_line_org_id_journal_line_id",
                table: "bank_line_status",
                columns: new[] { "org_id", "journal_line_id" },
                principalTable: "journal_lines",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_bank_line_status_reconciliation_org_id_reconciliation_id",
                table: "bank_line_status",
                columns: new[] { "org_id", "reconciliation_id" },
                principalTable: "bank_reconciliations",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_bulk_run_items_bulk_runs_org_id_run_id",
                table: "bulk_run_items",
                columns: new[] { "org_id", "run_id" },
                principalTable: "bulk_runs",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_import_batches_import_batches_org_id_supersedes_batch_id",
                table: "import_batches",
                columns: new[] { "org_id", "supersedes_batch_id" },
                principalTable: "import_batches",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_import_rows_import_batches_org_id_batch_id",
                table: "import_rows",
                columns: new[] { "org_id", "batch_id" },
                principalTable: "import_batches",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_journal_entries_journal_entries_org_id_reverses_entry_id",
                table: "journal_entries",
                columns: new[] { "org_id", "reverses_entry_id" },
                principalTable: "journal_entries",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_accounts_org_id_account_id",
                table: "journal_lines",
                columns: new[] { "org_id", "account_id" },
                principalTable: "accounts",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_journal_entries_org_id_entry_id",
                table: "journal_lines",
                columns: new[] { "org_id", "entry_id" },
                principalTable: "journal_entries",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_lease_lite_tenant_org_id_tenant_id",
                table: "lease_lite",
                columns: new[] { "org_id", "tenant_id" },
                principalTable: "tenants",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_lease_lite_unit_org_id_unit_id",
                table: "lease_lite",
                columns: new[] { "org_id", "unit_id" },
                principalTable: "units",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_properties_owners_org_id_owner_id",
                table: "properties",
                columns: new[] { "org_id", "owner_id" },
                principalTable: "owners",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_statement_lines_statement_imports_org_id_import_id",
                table: "statement_lines",
                columns: new[] { "org_id", "import_id" },
                principalTable: "statement_imports",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_statement_matches_statement_lines_org_id_statement_line_id",
                table: "statement_matches",
                columns: new[] { "org_id", "statement_line_id" },
                principalTable: "statement_lines",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_units_properties_org_id_property_id",
                table: "units",
                columns: new[] { "org_id", "property_id" },
                principalTable: "properties",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_bank_line_status_journal_line_org_id_journal_line_id",
                table: "bank_line_status");

            migrationBuilder.DropForeignKey(
                name: "fk_bank_line_status_reconciliation_org_id_reconciliation_id",
                table: "bank_line_status");

            migrationBuilder.DropForeignKey(
                name: "fk_bulk_run_items_bulk_runs_org_id_run_id",
                table: "bulk_run_items");

            migrationBuilder.DropForeignKey(
                name: "fk_import_batches_import_batches_org_id_supersedes_batch_id",
                table: "import_batches");

            migrationBuilder.DropForeignKey(
                name: "fk_import_rows_import_batches_org_id_batch_id",
                table: "import_rows");

            migrationBuilder.DropForeignKey(
                name: "fk_journal_entries_journal_entries_org_id_reverses_entry_id",
                table: "journal_entries");

            migrationBuilder.DropForeignKey(
                name: "fk_journal_lines_accounts_org_id_account_id",
                table: "journal_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_journal_lines_journal_entries_org_id_entry_id",
                table: "journal_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_lease_lite_tenant_org_id_tenant_id",
                table: "lease_lite");

            migrationBuilder.DropForeignKey(
                name: "fk_lease_lite_unit_org_id_unit_id",
                table: "lease_lite");

            migrationBuilder.DropForeignKey(
                name: "fk_properties_owners_org_id_owner_id",
                table: "properties");

            migrationBuilder.DropForeignKey(
                name: "fk_statement_lines_statement_imports_org_id_import_id",
                table: "statement_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_statement_matches_statement_lines_org_id_statement_line_id",
                table: "statement_matches");

            migrationBuilder.DropForeignKey(
                name: "fk_units_properties_org_id_property_id",
                table: "units");

            migrationBuilder.DropIndex(
                name: "ix_statement_matches_org_id_statement_line_id",
                table: "statement_matches");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_statement_lines_org_id_id",
                table: "statement_lines");

            migrationBuilder.DropIndex(
                name: "ix_statement_lines_org_id_import_id",
                table: "statement_lines");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_statement_imports_org_id_id",
                table: "statement_imports");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_journal_lines_org_id_id",
                table: "journal_lines");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_import_batches_org_id_id",
                table: "import_batches");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_bulk_runs_org_id_id",
                table: "bulk_runs");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_bank_reconciliations_org_id_id",
                table: "bank_reconciliations");

            migrationBuilder.DropIndex(
                name: "ix_bank_line_status_org_id_journal_line_id",
                table: "bank_line_status");

            migrationBuilder.DropIndex(
                name: "ix_bank_line_status_org_id_reconciliation_id",
                table: "bank_line_status");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_accounts_org_id_id",
                table: "accounts");

            migrationBuilder.CreateIndex(
                name: "ix_units_property_id",
                table: "units",
                column: "property_id");

            migrationBuilder.CreateIndex(
                name: "ix_properties_owner_id",
                table: "properties",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_lease_lite_tenant_id",
                table: "lease_lite",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_lease_lite_unit_id",
                table: "lease_lite",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_journal_lines_account_id",
                table: "journal_lines",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_journal_lines_entry_id",
                table: "journal_lines",
                column: "entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_reverses_entry_id",
                table: "journal_entries",
                column: "reverses_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_rows_batch_id",
                table: "import_rows",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_supersedes_batch_id",
                table: "import_batches",
                column: "supersedes_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_bulk_run_items_run_id",
                table: "bulk_run_items",
                column: "run_id");

            migrationBuilder.AddForeignKey(
                name: "fk_bank_line_status_bank_reconciliation_reconciliation_id",
                table: "bank_line_status",
                column: "reconciliation_id",
                principalTable: "bank_reconciliations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_bank_line_status_journal_line_journal_line_id",
                table: "bank_line_status",
                column: "journal_line_id",
                principalTable: "journal_lines",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_bulk_run_items_bulk_runs_run_id",
                table: "bulk_run_items",
                column: "run_id",
                principalTable: "bulk_runs",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_import_batches_import_batches_supersedes_batch_id",
                table: "import_batches",
                column: "supersedes_batch_id",
                principalTable: "import_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_import_rows_import_batches_batch_id",
                table: "import_rows",
                column: "batch_id",
                principalTable: "import_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_journal_entries_journal_entries_reverses_entry_id",
                table: "journal_entries",
                column: "reverses_entry_id",
                principalTable: "journal_entries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_accounts_account_id",
                table: "journal_lines",
                column: "account_id",
                principalTable: "accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_journal_lines_journal_entries_entry_id",
                table: "journal_lines",
                column: "entry_id",
                principalTable: "journal_entries",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_lease_lite_tenant_tenant_id",
                table: "lease_lite",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_lease_lite_unit_unit_id",
                table: "lease_lite",
                column: "unit_id",
                principalTable: "units",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_properties_owners_owner_id",
                table: "properties",
                column: "owner_id",
                principalTable: "owners",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_statement_lines_statement_imports_import_id",
                table: "statement_lines",
                column: "import_id",
                principalTable: "statement_imports",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_statement_matches_statement_lines_statement_line_id",
                table: "statement_matches",
                column: "statement_line_id",
                principalTable: "statement_lines",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_units_properties_property_id",
                table: "units",
                column: "property_id",
                principalTable: "properties",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
