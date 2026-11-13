using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Tests;

/// <summary>Изменение и удаление операций из журнала «Финансы».</summary>
public class CashOperationEditTests
{
    private static async Task<decimal> BalanceAsync(TestDb t, int cashId) =>
        (await t.Finance.GetCashRegistersAsync()).First(c => c.Id == cashId).Balance;

    [Fact]
    public async Task Update_recalculates_balance_and_matches_ledger()
    {
        using var t = new TestDb();
        var income = await t.Finance.AddOperationAsync(t.CashId, CashDirection.Income, "Прочий доход", 100m, null);
        var expense = await t.Finance.AddOperationAsync(t.CashId, CashDirection.Expense, "Аренда", 30m, null);

        await t.Finance.UpdateOperationAsync(expense.Id, t.CashId, "Зарплата", 50m, "исправлено");

        Assert.Equal(50m, await BalanceAsync(t, t.CashId));
        Assert.Equal(50m, await t.Finance.CalculateLedgerBalanceAsync(t.CashId));
        var op = (await t.Finance.GetOperationsAsync(DateTime.Today, DateTime.Today)).First(o => o.Id == expense.Id);
        Assert.Equal("Зарплата", op.Category);
        Assert.Equal("исправлено", op.Comment);
    }

    [Fact]
    public async Task Delete_returns_amount_and_refuses_negative_balance()
    {
        using var t = new TestDb();
        var income = await t.Finance.AddOperationAsync(t.CashId, CashDirection.Income, "Прочий доход", 100m, null);
        var expense = await t.Finance.AddOperationAsync(t.CashId, CashDirection.Expense, "Аренда", 80m, null);

        // Удалить приход нельзя: в кассе станет −80
        await Assert.ThrowsAsync<BusinessException>(() => t.Finance.DeleteOperationAsync(income.Id));
        Assert.Equal(20m, await BalanceAsync(t, t.CashId));

        await t.Finance.DeleteOperationAsync(expense.Id);
        Assert.Equal(100m, await BalanceAsync(t, t.CashId));
        Assert.Equal(100m, await t.Finance.CalculateLedgerBalanceAsync(t.CashId));
    }

    [Fact]
    public async Task Document_operations_cannot_be_modified()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Чай");
        var s = await t.SupplierAsync();
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Income, "Начальный остаток", 500m, null);
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 2, 10m)], 20m, t.CashId));
        var payment = (await t.Finance.GetOperationsAsync(DateTime.Today, DateTime.Today)).First(o => o.Kind == CashOperationKind.SupplierPayment);

        Assert.False(FinanceService.CanModify(payment));
        await Assert.ThrowsAsync<BusinessException>(() => t.Finance.DeleteOperationAsync(payment.Id));
    }
}
