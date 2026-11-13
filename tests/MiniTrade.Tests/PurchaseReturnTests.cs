using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Tests;

/// <summary>Возврат товара поставщику.</summary>
public class PurchaseReturnTests
{
    private static decimal Cash(TestDb t) => t.Query(db => db.CashRegisters.First().Balance);

    [Fact]
    public async Task Unpaid_return_reduces_debt_and_stock_without_cash()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Чай");
        var s = await t.SupplierAsync();
        var doc = await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 10m)]));
        var itemId = t.Query(db => db.PurchaseItems.Single().Id);

        var ret = await t.Purchases.CreateReturnAsync(new PurchaseReturnRequest(doc.Id, [new PurchaseReturnLine(itemId, 3)]));

        Assert.Equal(30m, ret.Total);
        Assert.Equal(0m, ret.RefundAmount);
        var purchase = t.Query(db => db.Purchases.Single());
        Assert.Equal(70m, purchase.Total);
        Assert.Equal(30m, purchase.ReturnedTotal);
        Assert.Equal(70m, purchase.Debt);
        Assert.Equal(7m, t.Query(db => db.StockBalances.Single().Quantity));
        Assert.Equal(0m, Cash(t));
    }

    [Fact]
    public async Task Paid_return_refunds_excess_to_cash_and_blocks_edit()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Чай");
        var s = await t.SupplierAsync();
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Income, "Начальный остаток", 1000m, null);
        var doc = await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 10m)], 80m, t.CashId));
        var itemId = t.Query(db => db.PurchaseItems.Single().Id);

        // Долг 20, возврат 50: 20 зачтено в долг, 30 вернулось в кассу
        var ret = await t.Purchases.CreateReturnAsync(new PurchaseReturnRequest(doc.Id, [new PurchaseReturnLine(itemId, 5)], t.CashId));

        Assert.Equal(30m, ret.RefundAmount);
        var purchase = t.Query(db => db.Purchases.Single());
        Assert.Equal(50m, purchase.Total);
        Assert.Equal(50m, purchase.PaidAmount);
        Assert.Equal(950m, Cash(t));
        Assert.Equal(950m, await t.Finance.CalculateLedgerBalanceAsync(t.CashId));

        await Assert.ThrowsAsync<BusinessException>(() => t.Purchases.CreateReturnAsync(
            new PurchaseReturnRequest(doc.Id, [new PurchaseReturnLine(itemId, 6)], t.CashId)));
        await Assert.ThrowsAsync<BusinessException>(() => t.Purchases.DeletePurchaseAsync(doc.Id));
    }
}
