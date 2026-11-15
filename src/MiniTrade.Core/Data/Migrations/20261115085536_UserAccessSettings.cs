using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniTrade.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserAccessSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AccessUntil",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AllowedCashRegisterIds",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AllowedWarehouseIds",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultCashRegisterId",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultWarehouseId",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxDiscountPercent",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WholesalePrice",
                table: "Products",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessUntil",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AllowedCashRegisterIds",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AllowedWarehouseIds",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DefaultCashRegisterId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DefaultWarehouseId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MaxDiscountPercent",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "WholesalePrice",
                table: "Products");
        }
    }
}
