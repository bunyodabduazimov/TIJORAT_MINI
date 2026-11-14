using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniTrade.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class WeightedProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsWeighted",
                table: "Products",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ScalePrefix",
                table: "CompanySettings",
                type: "TEXT",
                nullable: true);

            // Существующие настройки: код весов по умолчанию
            migrationBuilder.Sql("UPDATE CompanySettings SET ScalePrefix = '20'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsWeighted",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ScalePrefix",
                table: "CompanySettings");
        }
    }
}
