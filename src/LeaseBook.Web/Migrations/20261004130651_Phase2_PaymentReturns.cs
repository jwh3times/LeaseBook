using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class Phase2_PaymentReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_operation_status",
                table: "payment_operations");

            migrationBuilder.AddColumn<DateTime>(
                name: "review_closed_at",
                table: "payment_operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "review_closed_by",
                table: "payment_operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_note",
                table: "payment_operations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_operation_status",
                table: "payment_operations",
                sql: "status IN ('Requested','Processing','Failed','Settled','NeedsReview','Returned','ReviewClosed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_operation_status",
                table: "payment_operations");

            migrationBuilder.DropColumn(
                name: "review_closed_at",
                table: "payment_operations");

            migrationBuilder.DropColumn(
                name: "review_closed_by",
                table: "payment_operations");

            migrationBuilder.DropColumn(
                name: "review_note",
                table: "payment_operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_operation_status",
                table: "payment_operations",
                sql: "status IN ('Requested','Processing','Failed','Settled','NeedsReview')");
        }
    }
}
