using System;
using Microsoft.EntityFrameworkCore.Migrations;
using LeaseBook.Web.Persistence;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class P2_OwnerAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "owner_access",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_owner_access", x => x.id);
                    table.ForeignKey(
                        name: "fk_owner_access_asp_net_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "asp_net_users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_owner_access_owner_org_id_owner_id",
                        columns: x => new { x.org_id, x.owner_id },
                        principalTable: "owners",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_owner_access_org_id_owner_id",
                table: "owner_access",
                columns: new[] { "org_id", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_owner_access_org_id_user_id",
                table: "owner_access",
                columns: new[] { "org_id", "user_id" },
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.EnableOrgRls("owner_access");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "owner_access");
        }
    }
}
