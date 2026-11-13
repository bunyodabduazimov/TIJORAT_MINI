using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Tests;

public class TradeServicesTests
{
    [Fact]
    public async Task Seed_creates_admin_warehouse_and_cash()
    {
        using var t = new TestDb();
        Assert.Equal(1, t.Query(db => db.Users.Count()));
        Assert.Equal(1, t.Query(db => db.Warehouses.Count()));
        Assert.Equal("TJS", t.Query(db => db.CompanySettings.First().Currency));
        var settings = new SettingsService(t, t.Session);
        Assert.NotNull(await settings.AuthenticateAsync("admin", "admin"));
        Assert.Null(await settings.AuthenticateAsync("admin", "wrong"));
    }

    [Fact]
    public async Task Purchase_increases_stock_with_weighted_average_cost_and_pays_from_cash()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Чай");
        var s = await t.SupplierAsync();
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Income, "Начальный остаток", 1000m, null);

        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 10m)], 100m, t.CashId));
        var second = await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 13m)], 50m, t.CashId));

        var row = (await t.Warehouse.GetBalancesAsync()).Single();
        Assert.Equal(20m, row.Quantity);
        Assert.Equal(11.5m, row.AvgCost);
        Assert.Equal(80m, second.Debt);
        Assert.Equal(850m, t.Query(db => db.CashRegisters.First().Balance));

        await t.Purchases.PayAsync(second.Id, 80m, t.CashId);
        Assert.Equal(770m, t.Query(db => db.CashRegisters.First().Balance));
        await Assert.ThrowsAsync<BusinessException>(() => t.Purchases.PayAsync(second.Id, 1m, t.CashId));
    }

    [Fact]
    public async Task Sale_decreases_stock_records_cost_and_increases_cash()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Сахар", 15m);
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 5, 10m)]));

        var sale = await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId,
            [new DocumentLine(p.Id, 2, 15m), new DocumentLine(p.Id, 1.5m, 14.99m)]));

        Assert.Equal("ПР-000001", sale.Number);
        Assert.Equal(52.49m, sale.Total); // 30 + 22.485 → 22.49
        Assert.Equal(35m, sale.CostTotal);
        Assert.Equal(1.5m, t.Query(db => db.StockBalances.Single().Quantity));
        Assert.Equal(52.49m, t.Query(db => db.CashRegisters.First().Balance));
        Assert.Equal(52.49m, await t.Finance.CalculateLedgerBalanceAsync(t.CashId));
    }

    [Fact]
    public async Task Sale_with_insufficient_stock_is_rejected_atomically()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Мука", 20m);
        var p2 = await t.ProductAsync("Рис", 20m);
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 3, 10m)]));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId,
            [new DocumentLine(p.Id, 2, 20m), new DocumentLine(p2.Id, 1, 20m)])));
        Assert.Contains("Недостаточно", ex.Message);
        // Ничего не изменилось: ни документа, ни движения, ни денег
        Assert.Equal(0, t.Query(db => db.Sales.Count()));
        Assert.Equal(3m, t.Query(db => db.StockBalances.Single(b => b.ProductId == p.Id).Quantity));
        Assert.Equal(0m, t.Query(db => db.CashRegisters.First().Balance));
    }

    [Fact]
    public async Task Return_restores_stock_and_refunds_cash_and_limits_quantity()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Масло", 50m);
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 30m)]));
        var sale = await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 4, 50m)]));
        var itemId = t.Query(db => db.SaleItems.Single().Id);

        var ret = await t.Sales.CreateReturnAsync(new SaleReturnRequest(sale.Id, t.CashId, [new ReturnLine(itemId, 1)]));
        Assert.Equal(50m, ret.Total);
        Assert.Equal(7m, t.Query(db => db.StockBalances.Single().Quantity));
        Assert.Equal(150m, t.Query(db => db.CashRegisters.First().Balance));
        await Assert.ThrowsAsync<BusinessException>(() =>
            t.Sales.CreateReturnAsync(new SaleReturnRequest(sale.Id, t.CashId, [new ReturnLine(itemId, 4)])));

        var profit = await t.Reports.GetProfitAsync(DateTime.Today, DateTime.Today);
        Assert.Equal(200m, profit.Revenue);
        Assert.Equal(50m, profit.Returns);
        Assert.Equal(90m, profit.CostOfSales);
        Assert.Equal(60m, profit.GrossProfit);
    }

    [Fact]
    public async Task Expense_cannot_exceed_cash_balance_and_reduces_profit()
    {
        using var t = new TestDb();
        await Assert.ThrowsAsync<BusinessException>(() =>
            t.Finance.AddOperationAsync(t.CashId, CashDirection.Expense, "Аренда", 100m, null));
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Income, "Внесение наличных", 300m, null);
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Expense, "Аренда", 120.555m, null);

        Assert.Equal(179.44m, t.Query(db => db.CashRegisters.First().Balance)); // 120.555 → 120.56
        var profit = await t.Reports.GetProfitAsync(DateTime.Today, DateTime.Today);
        Assert.Equal(120.56m, profit.Expenses);
        Assert.Equal(-120.56m, profit.NetProfit);
    }

    [Fact]
    public async Task Cash_transfer_moves_money_between_registers()
    {
        using var t = new TestDb();
        var second = await t.Catalog.SaveAsync(new CashRegister { Name = "Сейф" });
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Income, "Внесение наличных", 500m, null);
        await t.Finance.TransferAsync(t.CashId, second.Id, 200m, null);
        Assert.Equal(300m, t.Query(db => db.CashRegisters.Single(c => c.Id == t.CashId).Balance));
        Assert.Equal(200m, t.Query(db => db.CashRegisters.Single(c => c.Id == second.Id).Balance));
        Assert.Equal(200m, await t.Finance.CalculateLedgerBalanceAsync(second.Id));
    }

    [Fact]
    public async Task Transfer_and_inventory_adjust_stock()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Соль");
        var s = await t.SupplierAsync();
        var w2 = await t.Catalog.SaveAsync(new Warehouse { Name = "Магазин" });
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 4m)]));

        await t.Warehouse.TransferAsync(t.WarehouseId, w2.Id, [new TransferLine(p.Id, 4)]);
        Assert.Equal(6m, await t.Warehouse.GetQuantityAsync(p.Id, t.WarehouseId));
        Assert.Equal(4m, await t.Warehouse.GetQuantityAsync(p.Id, w2.Id));
        await Assert.ThrowsAsync<BusinessException>(() => t.Warehouse.TransferAsync(w2.Id, t.WarehouseId, [new TransferLine(p.Id, 5)]));

        var inv = await t.Warehouse.CreateInventoryAsync(t.WarehouseId, [new InventoryLine(p.Id, 5)]);
        Assert.Equal(-4m, inv.DifferenceAmount);
        Assert.Equal(5m, await t.Warehouse.GetQuantityAsync(p.Id, t.WarehouseId));
        // Сумма журнала движений совпадает с остатками
        var ledger = t.Query(db => db.StockMovements.Where(m => m.ProductId == p.Id).AsEnumerable().Sum(m => m.Quantity));
        Assert.Equal(9m, ledger);
    }

    [Fact]
    public async Task Used_product_is_archived_instead_of_deleted()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Кофе");
        var unused = await t.ProductAsync("Какао");
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 1, 1m)]));

        await t.Catalog.DeleteAsync<Product>(p.Id);
        await t.Catalog.DeleteAsync<Product>(unused.Id);
        // Мягкое удаление: записи остаются в базе, но скрыты из справочника
        Assert.True(t.Query(db => db.Products.Single(x => x.Id == p.Id).IsDeleted));
        Assert.True(t.Query(db => db.Products.Single(x => x.Id == unused.Id).IsDeleted));
        Assert.DoesNotContain(await t.Catalog.GetAllAsync<Product>(), x => x.Id == p.Id || x.Id == unused.Id);
    }

    [Fact]
    public async Task Dashboard_summary_is_consistent()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Вода", 5m);
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 3m)]));
        await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 2, 5m)]));

        var summary = await new DashboardService(t).GetSummaryAsync();
        Assert.Equal(10m, summary.TodayRevenue);
        Assert.Equal(4m, summary.MonthProfit);
        Assert.Equal(10m, summary.CashTotal);
        Assert.Equal(24m, summary.StockCostValue);
        Assert.Equal(30m, summary.SupplierDebt);
        Assert.Equal(7, summary.Last7Days.Count);
    }
}
