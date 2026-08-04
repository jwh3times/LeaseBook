using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <summary>
    /// Deliberately EMPTY. This migration exists only to carry the regenerated model snapshot, not to
    /// change the schema.
    /// <para>
    /// <c>M8_AddPlatformCapabilities</c> creates the four platform tables in hand-authored DDL,
    /// because their RLS policies, the append-only REVOKEs and the FKs to <c>orgs</c> cannot be
    /// expressed in the EF model (ADR-028; <c>Org</c> lives in the host, so a module configuration
    /// cannot declare a relationship to it). That migration was authored before the entity classes
    /// existed, so the snapshot it left behind knows nothing about those tables. Adding the entities
    /// in this change therefore made the model differ from the snapshot, and
    /// <c>has-pending-model-changes</c> reported it.
    /// </para>
    /// <para>
    /// EF's answer to that diff is the four <c>CreateTable</c> calls it generated here — which would
    /// fail on any database that has already run <c>M8_AddPlatformCapabilities</c>, and would create
    /// the tables WITHOUT their RLS on any database that has not. So the operations are removed and
    /// the Designer/snapshot are kept: the schema keeps coming from the hand-authored migration,
    /// while the snapshot becomes truthful again and EF's drift detection stays live for these
    /// tables. The alternative — <c>ExcludeFromMigrations()</c> — would have blinded
    /// <c>has-pending-model-changes</c> to them permanently, which is the drift this reconciliation
    /// exists to prevent.
    /// </para>
    /// <para>
    /// One residual, deliberately accepted: the snapshot describes <c>entitlements</c> and
    /// <c>capability_cohorts</c> without their FK to <c>orgs</c>, since the model cannot name
    /// <c>Org</c>. The differ compares model to snapshot, so it never emits an operation about a
    /// constraint neither one knows — but a future migration that RECREATES either table must
    /// re-add the FK by hand.
    /// </para>
    /// </summary>
    public partial class M8_ReconcileCapabilitiesModelSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — see the class summary. The four platform tables and their RLS are
            // created by M8_AddPlatformCapabilities; this migration only reconciles the model snapshot.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: this migration made no schema change.
        }
    }
}
