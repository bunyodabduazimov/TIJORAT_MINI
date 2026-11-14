using Microsoft.EntityFrameworkCore;
using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Tests;

/// <summary>
/// Проверки целостности учёта: регистры совпадают с журналами, балансы контрагентов одинаковы во всех местах
/// (справочник, выписка, баланс на дату, главная), партии FIFO совпадают с остатками.
/// </summary>
internal static class Integrity
{
    public static async Task AssertConsistentAsync(TestDb t, string? step = null)
    {
        var at = step is null ? "" : $" [{step}]";
        using (var db = t.CreateDbContext())
        {
            // Кассы: остаток = сумма журнала, в минус не уходит
            var txs = await db.CashTransactions.AsNoTracking().ToListAsync();
            foreach (var c in await db.CashRegisters.AsNoTracking().ToListAsync())
            {
                var ledger = Money.Round(txs.Where(x => x.CashRegisterId == c.Id).Sum(x => x.SignedAmount));
                Assert.True(c.Balance == ledger, $"Касса «{c.Name}»: остаток {c.Balance}, по журналу {ledger}{at}");
                Assert.True(c.Balance >= 0, $"Касса «{c.Name}» в минусе: {c.Balance}{at}");
            }

            // Склад: остаток = сумма движений; партии = положительный остаток
            var moves = await db.StockMovements.AsNoTracking().ToListAsync();
            var lots = await db.StockLots.AsNoTracking().Where(l => l.Quantity > 0).ToListAsync();
            foreach (var b in await db.StockBalances.AsNoTracking().ToListAsync())
            {
                var m = moves.Where(x => x.ProductId == b.ProductId && x.WarehouseId == b.WarehouseId).Sum(x => x.Quantity);
                Assert.True(b.Quantity == m, $"Товар {b.ProductId}/склад {b.WarehouseId}: остаток {b.Quantity}, по движениям {m}{at}");
                var l = lots.Where(x => x.ProductId == b.ProductId && x.WarehouseId == b.WarehouseId).Sum(x => x.Quantity);
                Assert.True(Math.Max(0, b.Quantity) == l, $"Товар {b.ProductId}/склад {b.WarehouseId}: остаток {b.Quantity}, в партиях {l}{at}");
            }
            Assert.True(lots.All(l => l.Quantity <= l.InitialQuantity), $"Партия больше прихода{at}");
        }

        // Контрагенты: справочник = баланс на дату = выписка
        var statements = new StatementService(t);
        var tomorrow = DateTime.Today.AddDays(1);
        var byDate = (await statements.GetCounterpartyBalancesAsync(tomorrow)).ToDictionary(r => r.Id, r => r.Balance);
        foreach (var c in (await t.Catalog.GetPageAsync<Counterparty>(null, true, 0, 100_000)).Items)
        {
            var d = byDate.GetValueOrDefault(c.Id);
            Assert.True(c.Balance == d, $"«{c.Name}»: в справочнике {c.Balance}, на дату {d}{at}");
            var st = await statements.GetCounterpartyStatementAsync(c.Id, DateTime.Today.AddYears(-3), tomorrow);
            Assert.True(c.Balance == st.Closing, $"«{c.Name}»: в справочнике {c.Balance}, по выписке {st.Closing}{at}");
        }

        // Кассы на дату и выписка по кассе = текущий остаток
        foreach (var r in await statements.GetCashBalancesAsync(tomorrow))
        {
            var balance = t.Query(db => db.CashRegisters.First(c => c.Id == r.Id).Balance);
            Assert.True(balance == r.Balance, $"Касса {r.Name}: остаток {balance}, на дату {r.Balance}{at}");
            var st = await statements.GetCashStatementAsync(r.Id, DateTime.Today.AddYears(-3), tomorrow);
            Assert.True(balance == st.Closing, $"Касса {r.Name}: остаток {balance}, по выписке {st.Closing}{at}");
        }
    }

    /// <summary>Баланс контрагента из справочника.</summary>
    public static async Task<decimal> BalanceAsync(TestDb t, int counterpartyId) =>
        (await t.Catalog.GetPageAsync<Counterparty>(null, true, 0, 100_000)).Items.First(c => c.Id == counterpartyId).Balance;

    public static decimal Cash(TestDb t) => t.Query(db => db.CashRegisters.AsNoTracking().ToList().Sum(c => c.Balance));
}

public class AccountingIntegrityTests
{
    private static async Task<(Product P, Counterparty Supplier, Counterparty Customer)> SetupAsync(TestDb t, decimal qty = 10, decimal cost = 10m)
    {
        var p = await t.ProductAsync("Товар", 50m);
        var s = await t.SupplierAsync();
        var c = await t.Catalog.SaveAsync(new Counterparty { Name = "Покупатель" });
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, qty, cost)]));
        return (p, s, c);
    }

    // ───────── Возвраты покупателей и взаиморасчёты ─────────

    [Fact]
    public async Task Return_of_credit_sale_reduces_customer_debt_and_does_not_pay_cash()
    {
        using var t = new TestDb();
        var (p, _, c) = await SetupAsync(t);
        var sale = await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 2, 50m)], c.Id, OnCredit: true));
        Assert.Equal(100m, await Integrity.BalanceAsync(t, c.Id));
        Assert.Equal(100m, await t.Sales.GetReturnDebtOffsetLimitAsync(sale.Id));

        var item = t.Query(db => db.SaleItems.First(i => i.SaleId == sale.Id));
        await t.Sales.CreateReturnAsync(new SaleReturnRequest(sale.Id, t.CashId, [new ReturnLine(item.Id, 1)]));

        Assert.Equal(50m, await Integrity.BalanceAsync(t, c.Id));
        Assert.Equal(0m, Integrity.Cash(t)); // раньше: «Недостаточно средств» или выдача денег при живом долге
        Assert.Equal(0, t.Query(db => db.CashTransactions.Count(x => x.Kind == CashOperationKind.SaleReturn)));
        Assert.Equal(9m, t.Query(db => db.StockBalances.Single().Quantity));
        await Integrity.AssertConsistentAsync(t);
    }

    [Fact]
    public async Task Return_of_partly_paid_sale_offsets_debt_first_then_pays_rest()
    {
        using var t = new TestDb();
        var (p, _, c) = await SetupAsync(t);
        var sale = await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 2, 50m)], c.Id,
            Payments: [new SalePayment(t.CashId, 30m)], OnCredit: true));
        Assert.Equal(70m, await Integrity.BalanceAsync(t, c.Id));
        Assert.Equal(30m, Integrity.Cash(t));

        var item = t.Query(db => db.SaleItems.First(i => i.SaleId == sale.Id));
        await t.Sales.CreateReturnAsync(new SaleReturnRequest(sale.Id, t.CashId, [new ReturnLine(item.Id, 2)]));

        Assert.Equal(0m, await Integrity.BalanceAsync(t, c.Id));
        Assert.Equal(0m, Integrity.Cash(t));
        var refund = t.Query(db => db.CashTransactions.Single(x => x.Kind == CashOperationKind.SaleReturn));
        Assert.Equal(30m, refund.Amount);
        Assert.Equal(c.Id, refund.CounterpartyId);
        await Integrity.AssertConsistentAsync(t);
    }

    [Fact]
    public async Task Two_partial_returns_of_credit_sale_do_not_offset_more_than_debt()
    {
        using var t = new TestDb();
        var (p, _, c) = await SetupAsync(t);
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Income, "Внесение наличных", 500m, null);
        var sale = await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 4, 50m)], c.Id,
            Payments: [new SalePayment(t.CashId, 120m)], OnCredit: true)); // 200, долг 80
        var item = t.Query(db => db.SaleItems.First(i => i.SaleId == sale.Id));

        await t.Sales.CreateReturnAsync(new SaleReturnRequest(sale.Id, t.CashId, [new ReturnLine(item.Id, 1)])); // 50 → в долг
        Assert.Equal(30m, await Integrity.BalanceAsync(t, c.Id));
        Assert.Equal(30m, await t.Sales.GetReturnDebtOffsetLimitAsync(sale.Id));
        await t.Sales.CreateReturnAsync(new SaleReturnRequest(sale.Id, t.CashId, [new ReturnLine(item.Id, 2)])); // 100: 30 в долг, 70 деньгами

        Assert.Equal(0m, await Integrity.BalanceAsync(t, c.Id));
        Assert.Equal(500m + 120m - 70m, Integrity.Cash(t));
        await Integrity.AssertConsistentAsync(t);
    }

    [Fact]
    public async Task Return_after_customer_paid_debt_gives_money_back()
    {
        using var t = new TestDb();
        var (p, _, c) = await SetupAsync(t);
        var sale = await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 2, 50m)], c.Id, OnCredit: true));
        await t.Finance.CounterpartyPaymentAsync(new CounterpartyPaymentRequest(CashDirection.Income, t.CashId, c.Id, 100m));
        Assert.Equal(0m, await Integrity.BalanceAsync(t, c.Id));
        Assert.Equal(0m, await t.Sales.GetReturnDebtOffsetLimitAsync(sale.Id));

        var item = t.Query(db => db.SaleItems.First(i => i.SaleId == sale.Id));
        await t.Sales.CreateReturnAsync(new SaleReturnRequest(sale.Id, t.CashId, [new ReturnLine(item.Id, 2)]));

        Assert.Equal(0m, await Integrity.BalanceAsync(t, c.Id));
        Assert.Equal(0m, Integrity.Cash(t));
        await Integrity.AssertConsistentAsync(t);
    }

    [Fact]
    public async Task Return_of_paid_sale_to_named_customer_pays_cash_and_keeps_balance()
    {
        using var t = new TestDb();
        var (p, _, c) = await SetupAsync(t);
        // У покупателя есть другой долг — возврат оплаченной продажи всё равно выдаётся деньгами
        await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 1, 50m)], c.Id, OnCredit: true));
        var sale = await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 2, 50m)], c.Id));
        Assert.Equal(0m, await t.Sales.GetReturnDebtOffsetLimitAsync(sale.Id));

        var item = t.Query(db => db.SaleItems.First(i => i.SaleId == sale.Id));
        await t.Sales.CreateReturnAsync(new SaleReturnRequest(sale.Id, t.CashId, [new ReturnLine(item.Id, 2)]));

        Assert.Equal(50m, await Integrity.BalanceAsync(t, c.Id));
        Assert.Equal(0m, Integrity.Cash(t));
        await Integrity.AssertConsistentAsync(t);
    }

    [Fact]
    public async Task Legacy_refunds_are_linked_to_customer_on_startup()
    {
        using var t = new TestDb();
        var (p, _, c) = await SetupAsync(t);
        var sale = await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 2, 50m)], c.Id));
        var item = t.Query(db => db.SaleItems.First(i => i.SaleId == sale.Id));
        await t.Sales.CreateReturnAsync(new SaleReturnRequest(sale.Id, t.CashId, [new ReturnLine(item.Id, 2)]));
        // Как в базе старой версии: выплата по возврату без контрагента
        using (var db = t.CreateDbContext())
        {
            await db.CashTransactions.Where(x => x.Kind == CashOperationKind.SaleReturn)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CounterpartyId, (int?)null));
        }
        Assert.Equal(-100m, await Integrity.BalanceAsync(t, c.Id));

        using (var db = t.CreateDbContext()) await MiniTrade.Core.Data.DbInitializer.SeedAsync(db);

        Assert.Equal(0m, await Integrity.BalanceAsync(t, c.Id));
        await Integrity.AssertConsistentAsync(t);
    }

    // ───────── Поставщики: аванс, долги ─────────

    [Fact]
    public async Task Supplier_overpayment_is_advance_everywhere()
    {
        using var t = new TestDb();
        var (p, s, _) = await SetupAsync(t); // закупка 100 без оплаты
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Income, "Внесение наличных", 1000m, null);
        Assert.Equal(-100m, await Integrity.BalanceAsync(t, s.Id));

        await t.Finance.CounterpartyPaymentAsync(new CounterpartyPaymentRequest(CashDirection.Expense, t.CashId, s.Id, 150m));

        Assert.Equal(50m, await Integrity.BalanceAsync(t, s.Id)); // раньше справочник показывал 0 — аванс терялся
        Assert.Equal(0m, await t.Finance.GetSupplierDebtAsync(s.Id));
        var row = Assert.Single(await t.Purchases.GetSupplierDebtsAsync());
        Assert.Equal(-50m, row.Debt);
        var dash = await new DashboardService(t).GetSummaryAsync();
        Assert.Equal(0m, dash.SupplierDebt);
        Assert.Equal(50m, dash.CustomerDebt);

        // Следующая закупка гасится авансом
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 8, 10m)]));
        Assert.Equal(-30m, await Integrity.BalanceAsync(t, s.Id));
        Assert.Equal(30m, await t.Finance.GetSupplierDebtAsync(s.Id));
        Assert.Equal(30m, (await new DashboardService(t).GetSummaryAsync()).SupplierDebt);
        await Integrity.AssertConsistentAsync(t);
    }

    [Fact]
    public async Task Purchase_payment_is_dated_by_document()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Товар");
        var s = await t.SupplierAsync();
        var past = DateTime.Today.AddDays(-10).AddHours(12);
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Income, "Внесение наличных", 100m, null, past.AddHours(-1));
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 10m)], 100m, t.CashId, Date: past));

        // На дату закупки и долга нет, и касса уже уменьшилась
        var balances = await new StatementService(t).GetCounterpartyBalancesAsync(past.Date);
        Assert.Equal(0m, balances.Single(b => b.Id == s.Id).Balance);
        Assert.Equal(0m, (await new StatementService(t).GetCashBalancesAsync(past.Date)).Single().Balance);
    }

    // ───────── Склад и себестоимость ─────────

    [Fact]
    public async Task Fifo_after_negative_stock_does_not_leave_phantom_lots()
    {
        using var t = new TestDb();
        await SetNegativeStockAsync(t, true);
        var p = await t.ProductAsync("Товар", 100m);
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 5, 10m)]));
        await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 8, 100m)])); // −3
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 10, 20m)])); // 7 по 20
        await Integrity.AssertConsistentAsync(t);
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 5, 30m)]));

        var sale = await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 12, 100m)]));

        Assert.Equal(7 * 20m + 5 * 30m, sale.CostTotal); // раньше 3 «лишние» единицы по 20 вытесняли партию по 30
        Assert.Equal(0m, t.Query(db => db.StockBalances.Single().Quantity));
        await Integrity.AssertConsistentAsync(t);
    }

    [Fact]
    public async Task Cancel_of_sale_made_into_negative_restores_stock_and_lots_exactly()
    {
        using var t = new TestDb();
        await SetNegativeStockAsync(t, true);
        var p = await t.ProductAsync("Товар", 100m);
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 2, 10m)]));
        var first = await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 5, 100m)])); // −3
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 4, 20m)])); // 1 по 20

        await t.Sales.DeleteSaleAsync(first.Id);

        Assert.Equal(6m, t.Query(db => db.StockBalances.Single().Quantity));
        await Integrity.AssertConsistentAsync(t);
    }

    [Fact]
    public async Task Inventory_shortage_is_valued_at_fifo_cost()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Товар", 100m);
        var s = await t.SupplierAsync();
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 5, 10m)]));
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(s.Id, t.WarehouseId, [new DocumentLine(p.Id, 5, 20m)]));

        var doc = await t.Warehouse.CreateInventoryAsync(t.WarehouseId, [new InventoryLine(p.Id, 7m)]);

        Assert.Equal(-30m, doc.DifferenceAmount); // 3 из первой партии по 10, а не по средней 15
        var row = (await t.Warehouse.GetBalancesAsync()).Single();
        Assert.Equal(7m, row.Quantity);
        Assert.Equal(2 * 10m + 5 * 20m, row.CostValue);
        await Integrity.AssertConsistentAsync(t);
    }

    // ───────── Аналитика ─────────

    [Fact]
    public async Task Profit_includes_write_offs_and_inventory_differences()
    {
        using var t = new TestDb();
        var (p, _, _) = await SetupAsync(t); // 10 по 10
        await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 2, 20m)]));
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Expense, "Аренда", 5m, null);
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Expense, ReportService.CashWithdrawal, 5m, null);
        await t.Warehouse.WriteOffAsync(t.WarehouseId, [new TransferLine(p.Id, 1)], "Брак");
        await t.Warehouse.CreateInventoryAsync(t.WarehouseId, [new InventoryLine(p.Id, 5m)]); // учёт 7, факт 5

        var profit = await t.Reports.GetProfitAsync(DateTime.Today, DateTime.Today);
        Assert.Equal(40m, profit.NetRevenue);
        Assert.Equal(20m, profit.GrossProfit);
        Assert.Equal(5m + 10m + 20m, profit.Expenses); // аренда + списание + недостача (изъятие — не расход)
        Assert.Equal(-15m, profit.NetProfit);
        Assert.Contains(profit.ExpenseItems, e => e.Category == ReportService.WriteOffArticle && e.Amount == 10m);
        Assert.Contains(profit.ExpenseItems, e => e.Category == ReportService.ShortageArticle && e.Amount == 20m);

        var dash = await new DashboardService(t).GetSummaryAsync();
        Assert.Equal(profit.NetProfit, dash.MonthProfit);
        Assert.Equal(profit.Expenses, dash.MonthExpenses);

        // Излишек — уменьшает расходы
        await t.Warehouse.CreateInventoryAsync(t.WarehouseId, [new InventoryLine(p.Id, 6m)]);
        var after = await t.Reports.GetProfitAsync(DateTime.Today, DateTime.Today);
        Assert.Contains(after.ExpenseItems, e => e.Category == ReportService.SurplusArticle && e.Amount == -10m);
        Assert.Equal(-5m, after.NetProfit);
    }

    [Fact]
    public async Task Dashboard_and_reports_agree_on_revenue_and_margin()
    {
        using var t = new TestDb();
        var (p, _, c) = await SetupAsync(t);
        var sale = await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 3, 50m)], c.Id, OnCredit: true));
        await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 1, 40m)]));
        var item = t.Query(db => db.SaleItems.First(i => i.SaleId == sale.Id));
        await t.Sales.CreateReturnAsync(new SaleReturnRequest(sale.Id, t.CashId, [new ReturnLine(item.Id, 1)]));

        var profit = await t.Reports.GetProfitAsync(DateTime.Today, DateTime.Today);
        var byProduct = Assert.Single(await t.Reports.GetSalesAsync(DateTime.Today, DateTime.Today, SalesGrouping.Product));
        var dash = await new DashboardService(t).GetSummaryAsync();

        Assert.Equal(140m, profit.NetRevenue); // 150 + 40 − 50
        Assert.Equal(30m, profit.CostOfSales); // 3 шт по 10
        Assert.Equal(profit.NetRevenue, byProduct.Revenue);
        Assert.Equal(profit.CostOfSales, byProduct.Cost);
        Assert.Equal(3m, byProduct.Quantity);
        Assert.Equal(profit.NetRevenue, dash.TodayRevenue);
        Assert.Equal(profit.NetProfit, dash.MonthProfit);
        Assert.Equal(100m, dash.CustomerDebt); // 150 в долг − 50 возврат
        Assert.Equal(100m, dash.SupplierDebt); // закупка 100 не оплачена
        Assert.Equal(40m, dash.CashTotal);
        Assert.Equal(70m, dash.StockCostValue);
        await Integrity.AssertConsistentAsync(t);
    }

    internal static async Task SetNegativeStockAsync(TestDb t, bool allow, CostingMethod? method = null)
    {
        using var db = t.CreateDbContext();
        var s = await db.CompanySettings.FirstAsync();
        s.AllowNegativeStock = allow;
        if (method is { } m) s.CostingMethod = m;
        await db.SaveChangesAsync();
    }
}
