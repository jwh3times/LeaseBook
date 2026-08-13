using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class M8_LinkLateFeeRentObligation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "assesses_entry_id",
                table: "journal_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_journal_entries_org_id_id",
                table: "journal_entries",
                columns: new[] { "org_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_org_id_assesses_entry_id",
                table: "journal_entries",
                columns: new[] { "org_id", "assesses_entry_id" },
                unique: true,
                filter: "assesses_entry_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_journal_entries_journal_entries_org_id_assesses_entry_id",
                table: "journal_entries",
                columns: new[] { "org_id", "assesses_entry_id" },
                principalTable: "journal_entries",
                principalColumns: new[] { "org_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_journal_entries_journal_entries_org_id_assesses_entry_id",
                table: "journal_entries");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_journal_entries_org_id_id",
                table: "journal_entries");

            migrationBuilder.DropIndex(
                name: "ix_journal_entries_org_id_assesses_entry_id",
                table: "journal_entries");

            migrationBuilder.DropColumn(
                name: "assesses_entry_id",
                table: "journal_entries");
        }
    }
}
