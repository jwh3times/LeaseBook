using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class M8_EnforceOneActiveLeasePerTenant : Migration
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
                columns: new[] { "org_id", "tenant_id" },
                unique: true,
                filter: "status = 'active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_lease_lite_org_id_tenant_id",
                table: "lease_lite");

            migrationBuilder.CreateIndex(
                name: "ix_lease_lite_org_id_tenant_id",
                table: "lease_lite",
                columns: new[] { "org_id", "tenant_id" });
        }
    }
}
