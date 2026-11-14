using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Tests;

public class ProductionServiceTests
{
    [Fact]
    public async Task Produce_consumes_materials_and_receives_output_at_full_cost()
    {
        using var t = new TestDb();
        var flour = await t.ProductAsync("Мука");
        var sugar = await t.ProductAsync("Сахар");
        var cake = await t.ProductAsync("Торт");
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId,
            [new DocumentLine(flour.Id, 10, 4m), new DocumentLine(sugar.Id, 5, 6m)]));

        var doc = await t.Production.ProduceAsync(new ProductionRequest(t.WarehouseId, cake.Id, 2,
            [new MaterialLine(flour.Id, 3), new MaterialLine(sugar.Id, 1), new MaterialLine(flour.Id, 1)], ExtraCost: 10m));

        Assert.Equal(2, doc.Items.Count);
        Assert.Equal(22m, doc.MaterialsCost); // 4×4 + 1×6
        Assert.Equal(32m, doc.TotalCost);
        Assert.Equal(16m, doc.UnitCost);
        Assert.Equal(6m, await t.Warehouse.GetQuantityAsync(flour.Id, t.WarehouseId));
        Assert.Equal(4m, await t.Warehouse.GetQuantityAsync(sugar.Id, t.WarehouseId));
        var stock = await t.Warehouse.GetWarehouseStockAsync(t.WarehouseId);
        Assert.Equal((2m, 16m), stock[cake.Id]);
    }

    [Fact]
    public async Task Produce_rejects_shortage_and_self_material()
    {
        using var t = new TestDb();
        var flour = await t.ProductAsync("Мука");
        var cake = await t.ProductAsync("Торт");
        await Assert.ThrowsAsync<BusinessException>(() => t.Production.ProduceAsync(
            new ProductionRequest(t.WarehouseId, cake.Id, 1, [new MaterialLine(flour.Id, 1)])));
        await Assert.ThrowsAsync<BusinessException>(() => t.Production.ProduceAsync(
            new ProductionRequest(t.WarehouseId, cake.Id, 1, [new MaterialLine(cake.Id, 1)])));
    }

    [Fact]
    public async Task Cancel_restores_materials_and_removes_output()
    {
        using var t = new TestDb();
        var flour = await t.ProductAsync("Мука");
        var cake = await t.ProductAsync("Торт");
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(flour.Id, 10, 4m)]));
        var doc = await t.Production.ProduceAsync(new ProductionRequest(t.WarehouseId, cake.Id, 1, [new MaterialLine(flour.Id, 5)]));

        await t.Production.CancelAsync(doc.Id);

        Assert.Equal(10m, await t.Warehouse.GetQuantityAsync(flour.Id, t.WarehouseId));
        Assert.Equal(0m, await t.Warehouse.GetQuantityAsync(cake.Id, t.WarehouseId));
        Assert.Empty(await t.Production.GetOrdersAsync());
        Assert.Empty(t.Query(db => db.StockMovements.Where(m => m.DocumentId == doc.Id
            && (m.Type == StockMovementType.ProductionConsume || m.Type == StockMovementType.ProductionOutput)).ToList()));
    }

    [Fact]
    public async Task Recipe_is_saved_with_merged_items_and_can_be_updated()
    {
        using var t = new TestDb();
        var flour = await t.ProductAsync("Мука");
        var cake = await t.ProductAsync("Торт");
        var r = await t.Production.SaveRecipeAsync(new Recipe { Name = "Торт", ProductId = cake.Id, OutputQuantity = 2 },
            [new MaterialLine(flour.Id, 1), new MaterialLine(flour.Id, 2)]);

        r = await t.Production.SaveRecipeAsync(new Recipe { Id = r.Id, Name = "Торт v2", ProductId = cake.Id, OutputQuantity = 4 },
            [new MaterialLine(flour.Id, 5)]);

        var saved = Assert.Single(await t.Production.GetRecipesAsync());
        Assert.Equal("Торт v2", saved.Name);
        Assert.Equal(5m, Assert.Single(saved.Items).Quantity);
        await Assert.ThrowsAsync<BusinessException>(() => t.Production.SaveRecipeAsync(
            new Recipe { Name = "X", ProductId = cake.Id }, [new MaterialLine(cake.Id, 1)]));
    }
}
