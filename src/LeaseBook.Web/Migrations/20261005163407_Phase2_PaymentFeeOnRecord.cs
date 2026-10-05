using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <inheritdoc />
    public partial class Phase2_PaymentFeeOnRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "method",
                table: "payment_operations",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "ach");

            migrationBuilder.AddColumn<DateTime>(
                name: "paid_at",
                table: "payment_operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "quoted_fee",
                table: "payment_operations",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_operation_method",
                table: "payment_operations",
                sql: "method IN ('card','ach')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_operation_quoted_fee",
                table: "payment_operations",
                sql: "quoted_fee >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_operation_method",
                table: "payment_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_operation_quoted_fee",
                table: "payment_operations");

            migrationBuilder.DropColumn(
                name: "method",
                table: "payment_operations");

            migrationBuilder.DropColumn(
                name: "paid_at",
                table: "payment_operations");

            migrationBuilder.DropColumn(
                name: "quoted_fee",
                table: "payment_operations");
        }
    }
}
