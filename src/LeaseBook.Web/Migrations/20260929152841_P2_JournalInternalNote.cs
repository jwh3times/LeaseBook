using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <summary>
    /// #468: a staff-only note beside the owner-facing description. Additive and nullable, no backfill:
    /// existing entries — including every pre-#468 "VOID: …" reversal — stay exactly as posted.
    /// </summary>
    public partial class P2_JournalInternalNote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "internal_note",
                table: "journal_entries",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "internal_note",
                table: "journal_entries");
        }
    }
}
