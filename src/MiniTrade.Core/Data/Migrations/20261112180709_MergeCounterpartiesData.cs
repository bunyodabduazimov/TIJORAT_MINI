using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniTrade.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MergeCounterpartiesData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Внешние ключи уже указывают на Counterparties: поставщики получают Id со сдвигом (как в MergeCounterparties)
            const string offset = "(SELECT COALESCE(MAX(Id), 0) FROM Customers)";
            migrationBuilder.Sql($"UPDATE Purchases SET SupplierId = SupplierId + {offset};");
            migrationBuilder.Sql("UPDATE CashTransactions SET CounterpartyId = (SELECT CpId FROM _CashCounterpartyMap WHERE TxId = CashTransactions.Id) WHERE Id IN (SELECT TxId FROM _CashCounterpartyMap);");
            migrationBuilder.Sql("DROP TABLE _CashCounterpartyMap;");
            migrationBuilder.Sql("DROP TABLE Customers;");
            migrationBuilder.Sql("DROP TABLE Suppliers;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Объединение контрагентов не откатывается.");
        }
    }
}
