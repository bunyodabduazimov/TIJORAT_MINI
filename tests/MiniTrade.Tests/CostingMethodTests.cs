using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Tests;

/// <summary>Метод себестоимости: FIFO (по умолчанию) и средняя.</summary>
public class CostingMethodTests
{
    private static async Task<(TestDb t, Product p)> TwoLotsAsync(CostingMethod method)
    {
        var t = new TestDb();
        t.Query(db => { db.CompanySettings.First().CostingMethod = method; return db.SaveChanges(); });
        var p = await t.ProductAsync("Чай", 20m);
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 10m)], Date: DateTime.Now.AddMinutes(-2)));
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 14m)], Date: DateTime.Now.AddMinutes(-1)));
        return (t, p);
    }

    [Fact]
    public void Default_method_is_fifo()
    {
        using var t = new TestDb();
        Assert.Equal(CostingMethod.Fifo, t.Query(db => db.CompanySettings.First().CostingMethod));
    }

    [Fact]
    public async Task Fifo_takes_oldest_lots_first()
    {
        var (t, p) = await TwoLotsAsync(CostingMethod.Fifo);
        using var _ = t;
        await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 15, 20m)]));

        var item = t.Query(db => db.SaleItems.Single());
        Assert.Equal(Math.Round((10 * 10m + 5 * 14m) / 15, 4), Math.Round(item.UnitCost, 4));
        var balance = t.Query(db => db.StockBalances.Single());
        Assert.Equal(5m, balance.Quantity);
        Assert.Equal(14m, balance.AvgCost); // остаток — из второй партии
        Assert.Equal(5m, t.Query(db => db.StockLots.ToList().Sum(l => l.Quantity)));
    }

    [Fact]
    public async Task Average_uses_weighted_cost()
    {
        var (t, p) = await TwoLotsAsync(CostingMethod.Average);
        using var _ = t;
        await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 15, 20m)]));

        Assert.Equal(12m, t.Query(db => db.SaleItems.Single().UnitCost));
        Assert.Equal(12m, t.Query(db => db.StockBalances.Single().AvgCost));
    }

    [Fact]
    public async Task Fifo_deleted_sale_restores_lots()
    {
        var (t, p) = await TwoLotsAsync(CostingMethod.Fifo);
        using var _ = t;
        var sale = await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 15, 20m)]));
        await t.Sales.DeleteSaleAsync(sale.Id);

        await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 10, 20m)]));
        Assert.Equal(10m, Math.Round(t.Query(db => db.SaleItems.Single().UnitCost), 4));
    }
}
