using System;
using Microsoft.EntityFrameworkCore.Migrations;
using LeaseBook.Web.Persistence;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class P2_ResidentAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_asp_net_users_org_id_id",
                table: "asp_net_users",
                columns: new[] { "org_id", "id" });

            migrationBuilder.CreateTable(
                name: "resident_access",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resident_access", x => x.id);
                    table.ForeignKey(
                        name: "fk_resident_access_asp_net_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "asp_net_users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_resident_access_tenant_org_id_tenant_id",
                        columns: x => new { x.org_id, x.tenant_id },
                        principalTable: "tenants",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_resident_access_org_id_tenant_id",
                table: "resident_access",
                columns: new[] { "org_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_resident_access_org_id_user_id",
                table: "resident_access",
                columns: new[] { "org_id", "user_id" },
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.EnableOrgRls("resident_access");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resident_access");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_asp_net_users_org_id_id",
                table: "asp_net_users");
        }
    }
}
