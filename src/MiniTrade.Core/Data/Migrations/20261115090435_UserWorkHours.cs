using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniTrade.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserWorkHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeSpan>(
                name: "WorkFrom",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "WorkTo",
                table: "Users",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkFrom",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "WorkTo",
                table: "Users");
        }
    }
}
