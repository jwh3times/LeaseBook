using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class M8_AddImportBatchSupersedesBatchId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "supersedes_batch_id",
                table: "import_batches",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_org_id_supersedes_batch_id",
                table: "import_batches",
                columns: new[] { "org_id", "supersedes_batch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_supersedes_batch_id",
                table: "import_batches",
                column: "supersedes_batch_id");

            migrationBuilder.AddForeignKey(
                name: "fk_import_batches_import_batches_supersedes_batch_id",
                table: "import_batches",
                column: "supersedes_batch_id",
                principalTable: "import_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_import_batches_import_batches_supersedes_batch_id",
                table: "import_batches");

            migrationBuilder.DropIndex(
                name: "ix_import_batches_org_id_supersedes_batch_id",
                table: "import_batches");

            migrationBuilder.DropIndex(
                name: "ix_import_batches_supersedes_batch_id",
                table: "import_batches");

            migrationBuilder.DropColumn(
                name: "supersedes_batch_id",
                table: "import_batches");
        }
    }
}
