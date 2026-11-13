using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Tests;

/// <summary>Платежи контрагентам из раздела «Финансы».</summary>
public class CounterpartyPaymentTests
{
    [Fact]
    public async Task Supplier_payment_settles_oldest_purchases_first_and_reduces_cash()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Чай");
        var s = await t.SupplierAsync();
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Income, "Начальный остаток", 500m, null);
        var first = await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 10m)], 0m, null));
        var second = await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 5, 10m)], 0m, null));
        Assert.Equal(150m, await t.Finance.GetSupplierDebtAsync(s.Id));

        await t.Finance.CounterpartyPaymentAsync(new CounterpartyPaymentRequest(CashDirection.Expense, t.CashId, s.Id, 120m));

        Assert.Equal(0m, t.Query(db => db.Purchases.First(x => x.Id == first.Id)).Debt);
        Assert.Equal(30m, t.Query(db => db.Purchases.First(x => x.Id == second.Id)).Debt);
        Assert.Equal(30m, await t.Finance.GetSupplierDebtAsync(s.Id));
        Assert.Equal(380m, t.Query(db => db.CashRegisters.First()).Balance);

        var op = (await t.Finance.GetOperationsAsync(DateTime.Today, DateTime.Today)).First(o => o.Kind == CashOperationKind.SupplierPayment);
        Assert.Equal(s.Name, op.CounterpartyName);
        Assert.Equal("Оплата контрагенту", op.Category);
    }

    [Fact]
    public async Task Customer_payment_is_income_with_counterparty()
    {
        using var t = new TestDb();
        var c = await t.Catalog.SaveAsync(new Counterparty { Name = "ООО Сомон" });

        await t.Finance.CounterpartyPaymentAsync(new CounterpartyPaymentRequest(CashDirection.Income, t.CashId, c.Id, 250m, null, "аванс"));

        Assert.Equal(250m, t.Query(db => db.CashRegisters.First()).Balance);
        var op = Assert.Single(await t.Finance.GetOperationsAsync(DateTime.Today, DateTime.Today));
        Assert.Equal("ООО Сомон", op.CounterpartyName);
        Assert.Equal("Оплата от контрагента", op.Category);
    }

    [Fact]
    public async Task Payment_requires_exactly_one_counterparty()
    {
        using var t = new TestDb();
        await Assert.ThrowsAsync<BusinessException>(() =>
            t.Finance.CounterpartyPaymentAsync(new CounterpartyPaymentRequest(CashDirection.Income, t.CashId, 0, 10m)));
    }
}
