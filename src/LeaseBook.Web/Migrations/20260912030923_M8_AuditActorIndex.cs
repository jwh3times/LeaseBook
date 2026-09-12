using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class M8_AuditActorIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_audit_events_org_id_actor_user_id_occurred_at",
                table: "audit_events",
                columns: new[] { "org_id", "actor_user_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_events_org_id_actor_user_id_occurred_at",
                table: "audit_events");
        }
    }
}
