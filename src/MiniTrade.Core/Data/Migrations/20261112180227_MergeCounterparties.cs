using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniTrade.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MergeCounterparties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CounterpartyGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CounterpartyGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Counterparties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: true),
                    Phone = table.Column<string>(type: "TEXT", nullable: true),
                    Address = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Counterparties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Counterparties_CounterpartyGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "CounterpartyGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Counterparties_GroupId",
                table: "Counterparties",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Counterparties_Name",
                table: "Counterparties",
                column: "Name");

            // Перенос данных: покупатели сохраняют Id, поставщики получают Id со сдвигом
            const string offset = "(SELECT COALESCE(MAX(Id), 0) FROM Customers)";
            migrationBuilder.Sql("INSERT INTO CounterpartyGroups (Id, Name, Description, IsActive) VALUES (1, 'Покупатели', NULL, 1), (2, 'Поставщики', NULL, 1);");
            migrationBuilder.Sql("INSERT INTO Counterparties (Id, Name, GroupId, Phone, Address, Note, IsActive) SELECT Id, Name, 1, Phone, Address, Note, IsActive FROM Customers;");
            migrationBuilder.Sql($"INSERT INTO Counterparties (Id, Name, GroupId, Phone, Address, Note, IsActive) SELECT Id + {offset}, Name, 2, Phone, Address, Note, IsActive FROM Suppliers;");
            // Контрагент кассовых операций: новые Id запоминаем до перестройки таблицы (применяются в MergeCounterpartiesData)
            migrationBuilder.Sql($"CREATE TABLE _CashCounterpartyMap AS SELECT Id AS TxId, CASE WHEN CustomerId IS NOT NULL THEN CustomerId ELSE SupplierId + {offset} END AS CpId FROM CashTransactions WHERE CustomerId IS NOT NULL OR SupplierId IS NOT NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_CashTransactions_Customers_CustomerId",
                table: "CashTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_CashTransactions_Suppliers_SupplierId",
                table: "CashTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_Purchases_Suppliers_SupplierId",
                table: "Purchases");

            migrationBuilder.DropForeignKey(
                name: "FK_Sales_Customers_CustomerId",
                table: "Sales");

            migrationBuilder.DropIndex(
                name: "IX_CashTransactions_CustomerId",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "CashTransactions");

            migrationBuilder.RenameColumn(
                name: "SupplierId",
                table: "CashTransactions",
                newName: "CounterpartyId");

            migrationBuilder.RenameIndex(
                name: "IX_CashTransactions_SupplierId",
                table: "CashTransactions",
                newName: "IX_CashTransactions_CounterpartyId");

            migrationBuilder.AddForeignKey(
                name: "FK_CashTransactions_Counterparties_CounterpartyId",
                table: "CashTransactions",
                column: "CounterpartyId",
                principalTable: "Counterparties",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Purchases_Counterparties_SupplierId",
                table: "Purchases",
                column: "SupplierId",
                principalTable: "Counterparties",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Sales_Counterparties_CustomerId",
                table: "Sales",
                column: "CustomerId",
                principalTable: "Counterparties",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Старые таблицы Customers/Suppliers удаляет следующая миграция MergeCounterpartiesData
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CashTransactions_Counterparties_CounterpartyId",
                table: "CashTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_Purchases_Counterparties_SupplierId",
                table: "Purchases");

            migrationBuilder.DropForeignKey(
                name: "FK_Sales_Counterparties_CustomerId",
                table: "Sales");

            migrationBuilder.DropTable(
                name: "Counterparties");

            migrationBuilder.DropTable(
                name: "CounterpartyGroups");

            migrationBuilder.RenameColumn(
                name: "CounterpartyId",
                table: "CashTransactions",
                newName: "SupplierId");

            migrationBuilder.RenameIndex(
                name: "IX_CashTransactions_CounterpartyId",
                table: "CashTransactions",
                newName: "IX_CashTransactions_SupplierId");

            migrationBuilder.AddColumn<int>(
                name: "CustomerId",
                table: "CashTransactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Address = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    Phone = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Address = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    Phone = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_CustomerId",
                table: "CashTransactions",
                column: "CustomerId");

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

            migrationBuilder.AddForeignKey(
                name: "FK_Purchases_Suppliers_SupplierId",
                table: "Purchases",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Sales_Customers_CustomerId",
                table: "Sales",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
