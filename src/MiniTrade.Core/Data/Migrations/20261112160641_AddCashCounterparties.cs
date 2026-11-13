using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniTrade.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCashCounterparties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CustomerId",
                table: "CashTransactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SupplierId",
                table: "CashTransactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_CustomerId",
                table: "CashTransactions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_SupplierId",
                table: "CashTransactions",
                column: "SupplierId");

            migrationBuilder.AddForeignKey(
                name: "FK_CashTransactions_Customers_CustomerId",
                table: "CashTransactions",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CashTransactions_Suppliers_SupplierId",
                table: "CashTransactions",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CashTransactions_Customers_CustomerId",
                table: "CashTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_CashTransactions_Suppliers_SupplierId",
                table: "CashTransactions");

            migrationBuilder.DropIndex(
                name: "IX_CashTransactions_CustomerId",
                table: "CashTransactions");

            migrationBuilder.DropIndex(
                name: "IX_CashTransactions_SupplierId",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "CashTransactions");
        }
    }
}
