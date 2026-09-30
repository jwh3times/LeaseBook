using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <summary>
    /// Per-persona row-level security for the tenant and owner portals (#314, ADR-048). Every
    /// org-scoped table gets the persona gate from <see cref="Rls.ApplyPersonaGate"/>: three RESTRICTIVE
    /// policies that AND with the unchanged org (or platform) policies, admit <c>staff</c> and
    /// <c>system</c> organization-wide, and admit a portal persona only through the grants below.
    /// <para>
    /// Policy-only: no row is read or rewritten, so there is no FORCE bracket to lift, and the model
    /// snapshot does not change. Idempotent against the deny-by-default gates the historical
    /// <c>EnableOrgRls</c> calls now emit on a fresh database — each policy is dropped before it is
    /// created, so a fresh database and an upgraded one converge on the same grant table.
    /// </para>
    /// <para>
    /// The grants are derived from the portal endpoints' real query footprints and pinned, predicate
    /// by predicate, in <c>SchemaGuardTests.PersonaGrants</c>; <c>PersonaIsolationTests</c> proves what
    /// they admit. Identity is always resolved at the database from the active link row for
    /// <c>app.user_id</c> — the application never sets an owner or tenant id — so revoking a link takes
    /// effect on the next statement.
    /// </para>
    /// </summary>
    public partial class P2_PersonaRls : Migration
    {
        private const string IsOwner = "(SELECT current_setting('app.persona', true)) = 'owner'";
        private const string IsTenant = "(SELECT current_setting('app.persona', true)) = 'tenant'";
        private const string MyUser = "NULLIF(current_setting('app.user_id', true), '')::uuid";

        /// <summary>
        /// The owner the caller's active owner link names — never a value the app supplies. Used only by
        /// the <c>owners</c> grant itself, which checks <c>is_system</c> on its own row: joining
        /// <c>owners</c> here would make that policy read its own table (infinite recursion).
        /// </summary>
        private const string MyOwnerLinks =
            "SELECT oa.owner_id FROM owner_access oa WHERE oa.user_id = " + MyUser + " AND oa.revoked_at IS NULL";

        /// <summary>
        /// The caller's owner as every other grant sees it: the active link joined to a non-system
        /// <c>owners</c> row. A link naming a system roll-up owner — which the foreign key allows —
        /// therefore reaches nothing (#314 review, L2).
        /// </summary>
        private const string MyOwners =
            "SELECT oa.owner_id FROM owner_access oa JOIN owners o ON o.id = oa.owner_id WHERE oa.user_id = " + MyUser +
            " AND oa.revoked_at IS NULL AND NOT o.is_system";

        /// <summary>The tenant the caller's active resident link names; the <c>tenants</c> grant's own form.</summary>
        private const string MyTenantLinks =
            "SELECT ra.tenant_id FROM resident_access ra WHERE ra.user_id = " + MyUser + " AND ra.revoked_at IS NULL";

        /// <summary>The caller's tenant through a non-system <c>tenants</c> row, as every other grant sees it.</summary>
        private const string MyTenants =
            "SELECT ra.tenant_id FROM resident_access ra JOIN tenants t ON t.id = ra.tenant_id WHERE ra.user_id = " + MyUser +
            " AND ra.revoked_at IS NULL AND NOT t.is_system";

        /// <summary>
        /// The only operation a tenant may create: a fresh request, in exactly the shape
        /// <c>PaymentEngine.SubmitAsync</c> writes, against the org's payment fixture (#314 review, M1).
        /// Every column the worker owns is pinned to its initial value — above all <c>provider_id</c>,
        /// because the observations grant keys on it. <c>amount</c> and <c>currency</c> are bounded by
        /// their own check constraints; <c>key</c>, <c>fingerprint</c> and the timestamps are the
        /// caller's to choose and grant nothing.
        /// </summary>
        private const string FreshPaymentRequest =
            "status = 'Requested' AND reason IS NULL AND provider_id IS NULL AND journal_id IS NULL " +
            "AND processed_count = 0 AND attempts = 0 AND last_attempt_at IS NULL AND lease_until IS NULL " +
            "AND lease_claim_id IS NULL AND EXISTS (SELECT 1 FROM payment_fixtures pf " +
            "WHERE pf.id = payment_operations.generation AND pf.bank_id = payment_operations.bank_id " +
            "AND pf.account = payment_operations.account)";

        /// <summary>Account-security events a portal user writes about itself (change password, enrol MFA).</summary>
        private const string OwnAccountSecurity =
            "actor_kind = 'user' AND actor_user_id = " + MyUser + " AND entity_type = 'account-security' " +
            "AND entity_id = " + MyUser + " AND action IN ('password-changed', 'mfa-enrolled')";

        private const string MyLeaseUnits = "SELECT l.unit_id FROM lease_lite l WHERE l.tenant_id IN (" + MyTenants + ")";

        private const string MyLeaseProperties = "SELECT u.property_id FROM units u WHERE u.id IN (" + MyLeaseUnits + ")";

        /// <summary>The 34 org-policy tables at this migration. The three capability tables follow.</summary>
        private static readonly string[] OrgScopedTables =
        [
            "accounting_periods", "accounts", "audit_events", "bank_accounts", "bank_csv_mappings",
            "bank_line_status", "bank_reconciliations", "bulk_run_items", "bulk_runs", "import_batches",
            "import_rows", "journal_entries", "journal_lines", "lease_lite", "migration_verifications",
            "org_settings", "owner_access", "owners", "payment_effects", "payment_fixtures",
            "payment_observations", "payment_operations", "properties", "property_ownership_transfers",
            "resident_access", "simulated_collections", "statement_artifacts", "statement_delivery_attempts",
            "statement_delivery_events", "statement_imports", "statement_lines", "statement_matches", "tenants",
            "units",
        ];

        /// <summary>The capability tables (ADR-028): their gate also admits the platform plane.</summary>
        private static readonly string[] PlatformTables = ["capability_cohorts", "entitlements", "platform_audit_events"];

        /// <summary>
        /// The grant table. Each entry is what a portal persona may read (USING, which also bounds the
        /// rows <c>FOR SHARE</c> may lock) and insert (WITH CHECK). No entry means deny-by-default.
        /// Updates and deletes are never granted: the gate's companion policies admit them only for
        /// <c>staff</c> and <c>system</c>.
        /// </summary>
        private static readonly (string Table, string Read, string Insert)[] Grants =
        [
            // The link tables: the caller's own rows, by user id alone — no subquery, so no recursion.
            ("owner_access", $"{IsOwner} AND user_id = {MyUser}", null),
            ("resident_access", $"{IsTenant} AND user_id = {MyUser}", null),

            // Owner portal: GetOwnerNames, GetOwnerLedger (lines + entries + its reversal EXISTS),
            // GetPropertyAddresses over the property ids on its own rows, GetIssuedOwnerStatements(+Document).
            ("owners", $"{IsOwner} AND NOT is_system AND id IN ({MyOwnerLinks})", null),
            ("statement_artifacts", $"{IsOwner} AND owner_id IN ({MyOwners})", null),

            // Tenant portal: GetResidentNames, GetTenantLedger (lines joined to accounts and entries),
            // and the payment flow — fixture check, GetPaymentEligibility (bank, tenant -> lease -> unit ->
            // property FOR SHARE -> ownership transfers), GetPayments (operations + their observations).
            ("tenants", $"{IsTenant} AND NOT is_system AND id IN ({MyTenantLinks})", null),
            ("lease_lite", $"{IsTenant} AND tenant_id IN ({MyTenants})", null),
            ("units", $"{IsTenant} AND id IN ({MyLeaseUnits})", null),
            ("property_ownership_transfers", $"{IsTenant} AND property_id IN ({MyLeaseProperties})", null),
            ("accounts", $"{IsTenant} AND id IN (SELECT jl.account_id FROM journal_lines jl WHERE jl.tenant_id IN ({MyTenants}))", null),
            // Organization-level rows a portal needs are still granted only to a caller with an effective
            // (active, non-system) link — never to the persona alone (#314 review, L2).
            ("bank_accounts", $"{IsTenant} AND EXISTS ({MyTenants}) AND id IN (SELECT pf.bank_id FROM payment_fixtures pf)", null),
            ("payment_fixtures", $"{IsTenant} AND EXISTS ({MyTenants})", null),
            // Observations carry no operation id, only the provider id — which is why a tenant can never
            // write one (FreshPaymentRequest): only the worker, as system, sets it on an operation.
            ("payment_observations",
                $"{IsTenant} AND provider_id IN (SELECT po.provider_id FROM payment_operations po WHERE po.tenant_id IN ({MyTenants}))", null),
            ("payment_operations",
                $"{IsTenant} AND tenant_id IN ({MyTenants})",
                $"{IsTenant} AND tenant_id IN ({MyTenants}) AND user_id = {MyUser} AND {FreshPaymentRequest}"),

            // Both portals.
            ("journal_lines", $"({IsOwner} AND owner_id IN ({MyOwners})) OR ({IsTenant} AND tenant_id IN ({MyTenants}))", null),
            ("journal_entries",
                $"({IsOwner} AND id IN (SELECT jl.entry_id FROM journal_lines jl WHERE jl.owner_id IN ({MyOwners}))) " +
                $"OR ({IsTenant} AND id IN (SELECT jl.entry_id FROM journal_lines jl WHERE jl.tenant_id IN ({MyTenants})))", null),
            ("properties",
                $"({IsOwner} AND (owner_id IN ({MyOwners}) OR id IN (SELECT jl.property_id FROM journal_lines jl WHERE jl.owner_id IN ({MyOwners})))) " +
                $"OR ({IsTenant} AND id IN ({MyLeaseProperties}))", null),
            ("org_settings", $"({IsOwner} AND EXISTS ({MyOwners})) OR ({IsTenant} AND EXISTS ({MyTenants}))", null),

            // Write-only: account-security events (change password, MFA enrollment) for both personas,
            // and the audit row of a tenant's own payment submission. Never read back.
            ("audit_events", null,
                $"({IsTenant} AND (({OwnAccountSecurity}) OR (actor_kind = 'user' AND actor_user_id = {MyUser} " +
                "AND entity_type = 'payment_operations' AND action = 'insert'))) " +
                $"OR ({IsOwner} AND {OwnAccountSecurity})"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in OrgScopedTables)
            {
                var grant = Grants.SingleOrDefault(g => g.Table == table);
                migrationBuilder.ApplyPersonaGate(table, grant.Read, grant.Insert);
            }

            foreach (var table in PlatformTables)
            {
                migrationBuilder.ApplyPersonaGate(table, platformEscape: true);
            }
        }

        /// <summary>
        /// Drops the gate from every table — the state every database was in before this migration.
        /// Not a return to deny-by-default: the code that ran before this migration sets no persona, so
        /// a deny-by-default gate would lock it out of every row.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in OrgScopedTables.Concat(PlatformTables))
            {
                migrationBuilder.DropPersonaGate(table);
            }
        }
    }
}
