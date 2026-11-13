using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Tests;

public class WarehouseServiceTests
{
    [Fact]
    public async Task Transfer_merges_duplicate_lines_moves_stock_and_keeps_cost()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Кола");
        var s = await t.SupplierAsync();
        var second = await t.Catalog.SaveAsync(new Warehouse { Name = "Второй" });
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 9m)]));

        var doc = await t.Warehouse.TransferAsync(t.WarehouseId, second.Id, [new TransferLine(p.Id, 2), new TransferLine(p.Id, 3)]);

        Assert.Single(doc.Items);
        Assert.Equal(5m, await t.Warehouse.GetQuantityAsync(p.Id, t.WarehouseId));
        Assert.Equal(5m, await t.Warehouse.GetQuantityAsync(p.Id, second.Id));
        var items = await t.Warehouse.GetTransferItemsAsync(doc.Id);
        Assert.Equal(9m, items.Single().UnitCost);
        Assert.Equal("Кола", items.Single().Product!.Name);

        var stock = await t.Warehouse.GetWarehouseStockAsync(second.Id);
        Assert.Equal((5m, 9m), stock[p.Id]);
    }

    [Fact]
    public async Task Transfer_rejects_more_than_available()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Кола");
        var second = await t.Catalog.SaveAsync(new Warehouse { Name = "Второй" });
        await Assert.ThrowsAsync<BusinessException>(() => t.Warehouse.TransferAsync(t.WarehouseId, second.Id, [new TransferLine(p.Id, 1)]));
        await Assert.ThrowsAsync<BusinessException>(() => t.Warehouse.TransferAsync(t.WarehouseId, 9999, [new TransferLine(p.Id, 1)]));
    }

    [Fact]
    public async Task Inventory_adjusts_stock_and_details_show_only_differences()
    {
        using var t = new TestDb();
        var a = await t.ProductAsync("A");
        var b = await t.ProductAsync("B");
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId,
            [new DocumentLine(a.Id, 10, 5m), new DocumentLine(b.Id, 4, 2m)]));

        var doc = await t.Warehouse.CreateInventoryAsync(t.WarehouseId, [new InventoryLine(a.Id, 8), new InventoryLine(b.Id, 4)]);

        Assert.Equal(-10m, doc.DifferenceAmount);
        Assert.Equal(8m, await t.Warehouse.GetQuantityAsync(a.Id, t.WarehouseId));
        var diff = await t.Warehouse.GetInventoryItemsAsync(doc.Id);
        Assert.Equal(a.Id, diff.Single().ProductId);
        Assert.Equal(2, (await t.Warehouse.GetInventoryItemsAsync(doc.Id, onlyDifferences: false)).Count);

        var moves = await t.Warehouse.GetMovementsAsync(DateTime.Today, DateTime.Today, type: StockMovementType.InventoryAdjust);
        Assert.Equal(-2m, moves.Single().Quantity);
        Assert.Equal(3, (await t.Warehouse.GetMovementsAsync(DateTime.Today, DateTime.Today, warehouseId: t.WarehouseId)).Count);
    }

    [Fact]
    public async Task Balances_project_category_and_hide_zero()
    {
        using var t = new TestDb();
        var cat = await t.Catalog.SaveAsync(new Category { Name = "Напитки" });
        var p = await t.Catalog.SaveAsync(new Product { Name = "Кола", CategoryId = cat.Id, MinStock = 10 });
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 8, 9m)]));

        var row = (await t.Warehouse.GetBalancesAsync()).Single();
        Assert.Equal("Напитки", row.Category);
        Assert.True(row.IsLow);
        Assert.Equal(72m, row.CostValue);

        await t.Warehouse.CreateInventoryAsync(t.WarehouseId, [new InventoryLine(p.Id, 0)]);
        Assert.Empty(await t.Warehouse.GetBalancesAsync());
        Assert.Single(await t.Warehouse.GetBalancesAsync(includeZero: true));
    }
}
