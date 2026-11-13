using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace MiniTrade.Core.Data.MySqlMigrations
{
    /// <inheritdoc />
    public partial class CostingFifo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CostingMethod",
                table: "CompanySettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "StockLots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    WarehouseId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    InitialQuantity = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DocumentType = table.Column<int>(type: "int", nullable: false),
                    DocumentId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockLots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockLots_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StockLots_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "StockLotUsages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DocumentType = table.Column<int>(type: "int", nullable: false),
                    DocumentId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockLotUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockLotUsages_StockLots_LotId",
                        column: x => x.LotId,
                        principalTable: "StockLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_StockLots_DocumentType_DocumentId",
                table: "StockLots",
                columns: new[] { "DocumentType", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockLots_ProductId_WarehouseId_Date",
                table: "StockLots",
                columns: new[] { "ProductId", "WarehouseId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_StockLots_WarehouseId",
                table: "StockLots",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_StockLotUsages_DocumentType_DocumentId",
                table: "StockLotUsages",
                columns: new[] { "DocumentType", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockLotUsages_LotId",
                table: "StockLotUsages",
                column: "LotId");

            // Текущие остатки — начальные партии по средней себестоимости
            migrationBuilder.Sql("INSERT INTO StockLots (ProductId, WarehouseId, Date, UnitCost, InitialQuantity, Quantity, DocumentType, DocumentId) " +
                "SELECT ProductId, WarehouseId, '2000-01-01 00:00:00', AvgCost, Quantity, Quantity, 0, 0 FROM StockBalances WHERE Quantity > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockLotUsages");

            migrationBuilder.DropTable(
                name: "StockLots");

            migrationBuilder.DropColumn(
                name: "CostingMethod",
                table: "CompanySettings");
        }
    }
}
