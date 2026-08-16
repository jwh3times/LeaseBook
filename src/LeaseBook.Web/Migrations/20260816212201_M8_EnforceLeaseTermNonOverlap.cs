using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class M8_EnforceLeaseTermNonOverlap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_lease_lite_org_id_tenant_id",
                table: "lease_lite");

            migrationBuilder.CreateIndex(
                name: "ix_lease_lite_org_id_tenant_id",
                table: "lease_lite",
                columns: new[] { "org_id", "tenant_id" });

            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            migrationBuilder.Sql(
                """
                ALTER TABLE lease_lite
                    ADD CONSTRAINT ex_lease_lite_tenant_term_non_overlap
                    EXCLUDE USING gist (
                        org_id WITH =,
                        tenant_id WITH =,
                        daterange(start_date, end_date, '[]') WITH &&
                    )
                    WHERE (status <> 'pending');

                ALTER TABLE lease_lite
                    ADD CONSTRAINT ex_lease_lite_unit_term_non_overlap
                    EXCLUDE USING gist (
                        org_id WITH =,
                        unit_id WITH =,
                        daterange(start_date, end_date, '[]') WITH &&
                    )
                    WHERE (status <> 'pending');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE lease_lite
                    DROP CONSTRAINT ex_lease_lite_tenant_term_non_overlap;
                ALTER TABLE lease_lite
                    DROP CONSTRAINT ex_lease_lite_unit_term_non_overlap;
                """);

            migrationBuilder.DropIndex(
                name: "ix_lease_lite_org_id_tenant_id",
                table: "lease_lite");

            migrationBuilder.CreateIndex(
                name: "ix_lease_lite_org_id_tenant_id",
                table: "lease_lite",
                columns: new[] { "org_id", "tenant_id" },
                unique: true,
                filter: "status = 'active'");
        }
    }
}
