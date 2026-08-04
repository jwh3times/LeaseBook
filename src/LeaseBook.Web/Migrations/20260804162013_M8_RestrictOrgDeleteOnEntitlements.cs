using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <summary>
    /// Deleting an organization must not destroy its grant history (ADR-028 §6).
    /// <para>
    /// <c>fk_entitlements_orgs_org_id</c> shipped <c>ON DELETE CASCADE</c>, which meant a single
    /// <c>DELETE FROM orgs</c> silently erased every entitlement event for that organization — the
    /// append-only record of what it was entitled to and when. That is the exact property §6 exists to
    /// preserve, and it contradicted the reasoning that gave <c>platform_audit_events.org_id</c> no
    /// foreign key at all: the record of what was done to an organization has to outlive the
    /// organization. <c>RESTRICT</c> makes the deletion fail loudly instead, which is the right
    /// failure — an organization with grant history is not a row to be deleted casually, and whoever
    /// needs to delete one now has to decide what happens to the history.
    /// </para>
    /// <para>
    /// <b><c>fk_capability_cohorts_orgs_org_id</c> deliberately stays <c>ON DELETE CASCADE</c>, and the
    /// difference is the point.</b> Cohort rows are mutable membership — current targeting state, freely
    /// added and removed, carrying no history of its own (the record of who changed a cohort lives in
    /// <c>platform_audit_events</c>, which has no foreign key at all and is therefore untouched by any
    /// organization deletion). Membership in a deleted organization is meaningless and should go with
    /// it; a grant EVENT is a record, and a record is not membership. Anyone tempted to make the two
    /// consistent should change the reasoning first: the tables differ because one is a log and the
    /// other is state.
    /// </para>
    /// <para>
    /// Hand-authored, like <c>M8_AddPlatformCapabilities</c>: neither foreign key exists in the EF
    /// model — <c>Org</c> lives in the host, so a module's entity configuration cannot name it and the
    /// host was deliberately not made to configure a module's entity — so EF's differ emits nothing
    /// here and the model snapshot is unchanged by design.
    /// <c>SchemaGuardTests.ExpectedPlatformForeignKeys</c> is the compensating control and is updated
    /// in the same change.
    /// </para>
    /// </summary>
    public partial class M8_RestrictOrgDeleteOnEntitlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE entitlements
                    DROP CONSTRAINT fk_entitlements_orgs_org_id;

                ALTER TABLE entitlements
                    ADD CONSTRAINT fk_entitlements_orgs_org_id
                    FOREIGN KEY (org_id) REFERENCES orgs (id) ON DELETE RESTRICT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE entitlements
                    DROP CONSTRAINT fk_entitlements_orgs_org_id;

                ALTER TABLE entitlements
                    ADD CONSTRAINT fk_entitlements_orgs_org_id
                    FOREIGN KEY (org_id) REFERENCES orgs (id) ON DELETE CASCADE;
                """);
        }
    }
}
