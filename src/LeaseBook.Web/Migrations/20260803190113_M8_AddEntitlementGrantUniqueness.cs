using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <summary>
    /// Makes "the latest entitlement row per (org, capability)" a well-defined read.
    /// <para>
    /// <c>entitlements</c> is append-only, so current state is the newest row by <c>effective_at</c>.
    /// Two rows at the same instant for the same org and capability leave that undefined, and there
    /// is no sound fallback tie-break: <c>Guid.CreateVersion7</c> carries a millisecond timestamp
    /// with random low bits and no monotonic counter, so ids minted within the same millisecond —
    /// which is exactly what an <c>effective_at</c> tie implies — sort arbitrarily. UNIQUE removes
    /// the case instead of ranking it: the second write is rejected. Two simultaneous grant events
    /// are meaningless anyway, and the table has no production data, so this costs nothing now.
    /// </para>
    /// <para>
    /// Replaces the non-unique index rather than sitting beside it — identical column list, so the
    /// unique btree serves every read the plain one did. Pinned by
    /// <c>SchemaGuardTests.ExpectedPlatformUniqueIndexes</c>.
    /// </para>
    /// <para>
    /// Fully EF-generated, unlike <c>M8_AddPlatformCapabilities</c>: an index carries no RLS, and
    /// the snapshot is truthful about these tables since
    /// <c>M8_ReconcileCapabilitiesModelSnapshot</c>.
    /// </para>
    /// </summary>
    public partial class M8_AddEntitlementGrantUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_entitlements_org_id_capability_effective_at",
                table: "entitlements");

            migrationBuilder.CreateIndex(
                name: "ux_entitlements_org_capability_effective_at",
                table: "entitlements",
                columns: new[] { "org_id", "capability", "effective_at" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_entitlements_org_capability_effective_at",
                table: "entitlements");

            migrationBuilder.CreateIndex(
                name: "ix_entitlements_org_id_capability_effective_at",
                table: "entitlements",
                columns: new[] { "org_id", "capability", "effective_at" });
        }
    }
}
