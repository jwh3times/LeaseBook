using System;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class M8_AddPropertyOwnershipTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "property_ownership_transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    source_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_property_ownership_transfers", x => x.id);
                    table.UniqueConstraint("ak_property_ownership_transfers_org_id_id", x => new { x.org_id, x.id });
                    table.CheckConstraint("ck_property_ownership_transfers_distinct_owners", "from_owner_id <> to_owner_id");
                    table.ForeignKey(
                        name: "fk_property_ownership_transfers_owners_org_id_from_owner_id",
                        columns: x => new { x.org_id, x.from_owner_id },
                        principalTable: "owners",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_property_ownership_transfers_owners_org_id_to_owner_id",
                        columns: x => new { x.org_id, x.to_owner_id },
                        principalTable: "owners",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_property_ownership_transfers_properties_org_id_property_id",
                        columns: x => new { x.org_id, x.property_id },
                        principalTable: "properties",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_property_ownership_transfers_org_id_from_owner_id",
                table: "property_ownership_transfers",
                columns: new[] { "org_id", "from_owner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_property_ownership_transfers_org_id_property_id_effective_d",
                table: "property_ownership_transfers",
                columns: new[] { "org_id", "property_id", "effective_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_property_ownership_transfers_org_id_source_ref",
                table: "property_ownership_transfers",
                columns: new[] { "org_id", "source_ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_property_ownership_transfers_org_id_to_owner_id",
                table: "property_ownership_transfers",
                columns: new[] { "org_id", "to_owner_id" });

            migrationBuilder.EnableOrgRls("property_ownership_transfers");
            migrationBuilder.RevokeAppendOnly("property_ownership_transfers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "property_ownership_transfers");
        }
    }
}
