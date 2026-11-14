using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniTrade.Core.Data.MySqlMigrations
{
    /// <inheritdoc />
    public partial class RolePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CashierPermissions",
                table: "CompanySettings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ManagerPermissions",
                table: "CompanySettings",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CashierPermissions",
                table: "CompanySettings");

            migrationBuilder.DropColumn(
                name: "ManagerPermissions",
                table: "CompanySettings");
        }
    }
}
