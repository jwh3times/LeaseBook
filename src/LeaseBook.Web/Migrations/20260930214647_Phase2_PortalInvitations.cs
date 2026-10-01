using System;
using Microsoft.EntityFrameworkCore.Migrations;
using LeaseBook.Web.Persistence;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class Phase2_PortalInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "portal_invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    persona = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    delivery_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    delivery_error = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    delivery_attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_portal_invitations", x => x.id);
                    table.CheckConstraint("ck_portal_invitations_delivery_attempts", "delivery_attempts >= 0");
                    table.CheckConstraint("ck_portal_invitations_delivery_status", "delivery_status IN ('pending', 'delivered', 'failed')");
                    table.CheckConstraint("ck_portal_invitations_status", "status IN ('pending', 'accepted', 'cancelled', 'replaced')");
                    table.CheckConstraint("ck_portal_invitations_target", "(persona = 'tenant' AND tenant_id IS NOT NULL AND owner_id IS NULL) OR (persona = 'owner' AND owner_id IS NOT NULL AND tenant_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_portal_invitations_asp_net_users_org_id_accepted_by_user_id",
                        columns: x => new { x.org_id, x.accepted_by_user_id },
                        principalTable: "asp_net_users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_portal_invitations_asp_net_users_org_id_created_by_user_id",
                        columns: x => new { x.org_id, x.created_by_user_id },
                        principalTable: "asp_net_users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_portal_invitations_owner_org_id_owner_id",
                        columns: x => new { x.org_id, x.owner_id },
                        principalTable: "owners",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_portal_invitations_tenant_org_id_tenant_id",
                        columns: x => new { x.org_id, x.tenant_id },
                        principalTable: "tenants",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_portal_invitations_org_id_accepted_by_user_id",
                table: "portal_invitations",
                columns: new[] { "org_id", "accepted_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_portal_invitations_org_id_created_by_user_id",
                table: "portal_invitations",
                columns: new[] { "org_id", "created_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_portal_invitations_org_id_owner_id",
                table: "portal_invitations",
                columns: new[] { "org_id", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_portal_invitations_org_id_tenant_id",
                table: "portal_invitations",
                columns: new[] { "org_id", "tenant_id" });

            migrationBuilder.EnableOrgRls("portal_invitations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "portal_invitations");
        }
    }
}
