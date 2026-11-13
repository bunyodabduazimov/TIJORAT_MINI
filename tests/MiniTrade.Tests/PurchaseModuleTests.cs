using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Tests;

/// <summary>Модуль «Закуп»: изменение, удаление, журнал, долги.</summary>
public class PurchaseModuleTests
{
    private static async Task<(TestDb t, Product p, Counterparty s)> SetupAsync(decimal cash = 1000m)
    {
        var t = new TestDb();
        var p = await t.ProductAsync("Чай");
        var s = await t.SupplierAsync();
        if (cash > 0) await t.Finance.AddOperationAsync(t.CashId, CashDirection.Income, "Начальный остаток", cash, null);
        return (t, p, s);
    }

    private static decimal Cash(TestDb t) => t.Query(db => db.CashRegisters.First().Balance);

    [Fact]
    public async Task Update_reposts_stock_keeps_number_and_payments()
    {
        var (t, p, s) = await SetupAsync();
        using var _ = t;
        var doc = await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 10m)], 40m, t.CashId));

        var updated = await t.Purchases.UpdatePurchaseAsync(doc.Id,
            new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 4, 12m)], 8m, t.CashId));

        Assert.Equal(doc.Number, updated.Number);
        Assert.Equal(48m, updated.Total);
        Assert.Equal(48m, updated.PaidAmount); // 40 сохранено + 8 доплата
        var row = (await t.Warehouse.GetBalancesAsync()).Single();
        Assert.Equal(4m, row.Quantity);
        Assert.Equal(12m, row.AvgCost);
        Assert.Equal(952m, Cash(t));
        Assert.Equal(952m, await t.Finance.CalculateLedgerBalanceAsync(t.CashId));
        Assert.Single(t.Query(db => db.StockMovements.Where(m => m.DocumentId == doc.Id && m.Type == StockMovementType.Purchase).ToList()));
    }

    [Fact]
    public async Task Update_cannot_reduce_total_below_paid()
    {
        var (t, p, s) = await SetupAsync();
        using var _ = t;
        var doc = await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 10m)], 100m, t.CashId));
        await Assert.ThrowsAsync<BusinessException>(() => t.Purchases.UpdatePurchaseAsync(doc.Id,
            new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 5, 10m)])));
        Assert.Equal(10m, t.Query(db => db.StockBalances.Single().Quantity)); // откат
    }

    [Fact]
    public async Task Delete_removes_stock_refunds_cash_and_restores_average_cost()
    {
        var (t, p, s) = await SetupAsync();
        using var _ = t;
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 10m)]));
        var second = await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 14m)], 100m, t.CashId));
        await t.Purchases.PayAsync(second.Id, 40m, t.CashId);
        Assert.Equal(860m, Cash(t));

        await t.Purchases.DeletePurchaseAsync(second.Id);

        var row = (await t.Warehouse.GetBalancesAsync()).Single();
        Assert.Equal(10m, row.Quantity);
        Assert.Equal(10m, row.AvgCost);
        Assert.Equal(1000m, Cash(t));
        Assert.Equal(1000m, await t.Finance.CalculateLedgerBalanceAsync(t.CashId));
        Assert.Equal(1, t.Query(db => db.Purchases.Count()));
    }

    [Fact]
    public async Task Delete_is_rejected_when_goods_already_sold()
    {
        var (t, p, s) = await SetupAsync(0);
        using var _ = t;
        var doc = await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 5, 10m)]));
        await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 3, 20m)]));

        await Assert.ThrowsAsync<BusinessException>(() => t.Purchases.DeletePurchaseAsync(doc.Id));
        Assert.Equal(2m, t.Query(db => db.StockBalances.Single().Quantity));
    }

    [Fact]
    public async Task Purchase_paid_from_finance_cannot_be_modified()
    {
        var (t, p, s) = await SetupAsync();
        using var _ = t;
        var doc = await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 10m)]));
        await t.Finance.CounterpartyPaymentAsync(new CounterpartyPaymentRequest(CashDirection.Expense, t.CashId, s.Id, 30m));

        await Assert.ThrowsAsync<BusinessException>(() => t.Purchases.DeletePurchaseAsync(doc.Id));
    }

    [Fact]
    public async Task Overpayment_is_rejected()
    {
        var (t, p, s) = await SetupAsync();
        using var _ = t;
        await Assert.ThrowsAsync<BusinessException>(() => t.Purchases.CreatePurchaseAsync(
            new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 1, 10m)], 11m, t.CashId)));
        Assert.Equal(0, t.Query(db => db.Purchases.Count()));
        Assert.Equal(1000m, Cash(t));
    }

    [Fact]
    public async Task Journal_filters_by_debt_supplier_and_search_with_totals()
    {
        var (t, p, s) = await SetupAsync();
        using var _ = t;
        var other = await t.SupplierAsync("Сомон Трейд");
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 1, 100m)], 100m, t.CashId));
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 1, 50m)], 20m, t.CashId));
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(other.Id, t.WarehouseId, [new DocumentLine(p.Id, 1, 30m)], Comment: "срочно"));

        var all = await t.Purchases.GetPurchasesPageAsync(new PurchaseFilter(DateTime.Today, DateTime.Today), 0, 2);
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(2, all.Items.Count);
        Assert.Equal(180m, all.Total);
        Assert.Equal(120m, all.Paid);
        Assert.Equal("ЗК-000003", all.Items[0].Number); // новые сверху

        var debt = await t.Purchases.GetPurchasesPageAsync(new PurchaseFilter(DateTime.Today, DateTime.Today, OnlyWithDebt: true), 0, 50);
        Assert.Equal(2, debt.TotalCount);
        Assert.Equal(60m, debt.Debt);

        var bySupplier = await t.Purchases.GetPurchasesPageAsync(new PurchaseFilter(DateTime.Today, DateTime.Today, SupplierId: other.Id), 0, 50);
        Assert.Single(bySupplier.Items);
        var search = await t.Purchases.GetPurchasesPageAsync(new PurchaseFilter(DateTime.Today, DateTime.Today, Search: "сомон"), 0, 50);
        Assert.Single(search.Items);
    }

    [Fact]
    public async Task Supplier_debts_group_by_id_and_payments_are_listed()
    {
        var (t, p, _) = await SetupAsync();
        using var __ = t;
        var a = await t.SupplierAsync("Двойник");
        var b = await t.SupplierAsync("Двойник");
        var doc = await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(a.Id, t.WarehouseId, [new DocumentLine(p.Id, 1, 100m)], 30m, t.CashId));
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(b.Id, t.WarehouseId, [new DocumentLine(p.Id, 1, 50m)]));
        await t.Purchases.PayAsync(doc.Id, 20m, t.CashId);

        var debts = await t.Purchases.GetSupplierDebtsAsync();
        Assert.Equal(2, debts.Count);
        Assert.Equal(50m, debts.Single(d => d.SupplierId == a.Id).Debt);
        Assert.Equal(50m, debts.Single(d => d.SupplierId == b.Id).Debt);

        var payments = await t.Purchases.GetPaymentsAsync(doc.Id);
        Assert.Equal([30m, 20m], payments.Select(x => x.Amount));
        Assert.All(payments, x => Assert.Equal(a.Id, x.CounterpartyId));
    }
}
