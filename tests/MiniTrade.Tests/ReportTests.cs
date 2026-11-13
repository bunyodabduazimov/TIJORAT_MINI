using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Tests;

public class ReportTests
{
    [Fact]
    public async Task Sales_report_nets_returns_and_assigns_shares_and_abc()
    {
        using var t = new TestDb();
        var a = await t.ProductAsync("Масло", 50m);
        var b = await t.ProductAsync("Соль", 5m);
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId,
            [new DocumentLine(a.Id, 10, 30m), new DocumentLine(b.Id, 10, 2m)]));
        var sale = await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId,
            [new DocumentLine(a.Id, 4, 50m), new DocumentLine(b.Id, 2, 5m)]));
        var itemId = t.Query(db => db.SaleItems.Single(i => i.ProductId == a.Id).Id);
        await t.Sales.CreateReturnAsync(new SaleReturnRequest(sale.Id, t.CashId, [new ReturnLine(itemId, 1)]));

        var rows = await t.Reports.GetSalesAsync(DateTime.Today, DateTime.Today, SalesGrouping.Product);
        Assert.Equal(2, rows.Count);
        var oil = rows[0];
        Assert.Equal("Масло", oil.Product);
        Assert.Equal(3m, oil.Quantity);
        Assert.Equal(150m, oil.Revenue);
        Assert.Equal(90m, oil.Cost);
        Assert.Equal(1, oil.Documents);
        Assert.Equal("A", oil.Abc);
        Assert.Equal(93.8m, oil.SharePercent); // 150 / 160
        Assert.Equal("B", rows[1].Abc);

        var byDay = await t.Reports.GetSalesAsync(DateTime.Today, DateTime.Today, SalesGrouping.Day);
        Assert.Equal(160m, Assert.Single(byDay).Revenue);

        var byCustomer = await t.Reports.GetSalesAsync(DateTime.Today, DateTime.Today, SalesGrouping.Customer);
        Assert.Equal(ReportService.RetailCustomer, Assert.Single(byCustomer).Product);

        var otherWarehouse = await t.Reports.GetSalesAsync(DateTime.Today, DateTime.Today, SalesGrouping.Product, warehouseId: -1);
        Assert.Empty(otherWarehouse);
    }

    [Fact]
    public async Task Cash_flow_restores_opening_balance_from_journal()
    {
        using var t = new TestDb();
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Income, "Внесение наличных", 300m, null);
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Expense, "Аренда", 100m, null);

        var today = await t.Reports.GetCashFlowAsync(DateTime.Today, DateTime.Today);
        var row = Assert.Single(today.Registers);
        Assert.Equal(0m, row.Opening);
        Assert.Equal(300m, row.Income);
        Assert.Equal(100m, row.Expense);
        Assert.Equal(200m, row.Closing);
        Assert.Contains(today.Categories, c => c.Category == "Аренда" && c.Amount == 100m && !c.IsIncome);

        // Период целиком в будущем: всё уже было до начала — остаток на начало = текущий
        var future = await t.Reports.GetCashFlowAsync(DateTime.Today.AddDays(1), DateTime.Today.AddDays(2));
        Assert.Equal(200m, Assert.Single(future.Registers).Opening);
        Assert.Equal(200m, future.Closing);
    }

    [Fact]
    public async Task Profit_report_counts_checks_and_average()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Масло", 50m);
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 30m)]));
        await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 1, 50m)]));
        await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 3, 50m)]));

        // Даты в обратном порядке тоже принимаются
        var profit = await t.Reports.GetProfitAsync(DateTime.Today, DateTime.Today.AddDays(-3));
        Assert.Equal(2, profit.Checks);
        Assert.Equal(100m, profit.AverageCheck);
        Assert.Equal(40m, profit.GrossMarginPercent);
    }
}
