using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniTrade.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SalePaidAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PaidAmount",
                table: "Sales",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            // Все прежние продажи были оплачены полностью
            migrationBuilder.Sql("UPDATE Sales SET PaidAmount = Total;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaidAmount",
                table: "Sales");
        }
    }
}
