using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Tests;

/// <summary>Изолированная SQLite в памяти с применёнными миграциями.</summary>
public sealed class TestDb : IDbContextFactory<TradeDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:;Foreign Keys=True");

    public TestDb()
    {
        _connection.Open();
        using var db = CreateDbContext();
        DbInitializer.InitializeAsync(db).GetAwaiter().GetResult();
    }

    public TradeDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<TradeDbContext>().UseSqlite(_connection).Options);

    public UserSession Session { get; } = new();

    public SalesService Sales => new(this, Session);
    public PurchaseService Purchases => new(this, Session);
    public WarehouseService Warehouse => new(this);
    public ProductionService Production => new(this, Session);
    public FinanceService Finance => new(this, Session);
    public CatalogService Catalog => new(this);
    public ReportService Reports => new(this);

    public int WarehouseId => Query(db => db.Warehouses.First().Id);
    public int CashId => Query(db => db.CashRegisters.First().Id);

    public T Query<T>(Func<TradeDbContext, T> f)
    {
        using var db = CreateDbContext();
        return f(db);
    }

    public async Task<Product> ProductAsync(string name, decimal salePrice = 0) =>
        await Catalog.SaveAsync(new Product { Name = name, SalePrice = salePrice });

    public async Task<Counterparty> SupplierAsync(string name = "Поставщик") => await Catalog.SaveAsync(new Counterparty { Name = name });

    public void Dispose() => _connection.Dispose();
}
