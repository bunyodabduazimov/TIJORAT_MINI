using Microsoft.EntityFrameworkCore;
using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Tests;

/// <summary>
/// Случайные последовательности операций (как в живом магазине) с проверкой целостности после каждого шага.
/// Отклонённая операция (нехватка товара/денег) не должна ничего менять — это тоже проверяется инвариантами.
/// В конце проверяется балансовое тождество: деньги + склад + взаиморасчёты = накопленный результат.
/// </summary>
public class AccountingSimulationTests
{
    public static TheoryData<int, CostingMethod, bool> Cases => new()
    {
        { 1, CostingMethod.Fifo, false },
        { 2, CostingMethod.Average, false },
        { 3, CostingMethod.Fifo, true },
        { 4, CostingMethod.Average, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Random_operations_keep_all_registers_consistent(int seed, CostingMethod method, bool allowNegative)
    {
        using var t = new TestDb();
        await AccountingIntegrityTests.SetNegativeStockAsync(t, allowNegative, method);
        var rnd = new Random(seed);

        var w1 = t.WarehouseId;
        var w2 = (await t.Catalog.SaveAsync(new Warehouse { Name = "Склад 2" })).Id;
        var c1 = t.CashId;
        var c2 = (await t.Catalog.SaveAsync(new CashRegister { Name = "Карта", IsCashless = true })).Id;
        int[] warehouses = [w1, w2], cashes = [c1, c2];
        var products = new List<int>();
        for (var i = 0; i < 6; i++) products.Add((await t.ProductAsync($"Товар {i}", 20m + i * 5)).Id);
        var service = await t.Catalog.SaveAsync(new Product { Name = "Доставка", SalePrice = 15m, IsService = true, PurchasePrice = 5m });
        var suppliers = new List<int>();
        for (var i = 0; i < 3; i++) suppliers.Add((await t.SupplierAsync($"Поставщик {i}")).Id);
        var customers = new List<int>();
        for (var i = 0; i < 3; i++) customers.Add((await t.Catalog.SaveAsync(new Counterparty { Name = $"Покупатель {i}" })).Id);
        await t.Finance.AddOperationAsync(c1, CashDirection.Income, "Начальный остаток", 3000m, null);

        T Pick<T>(IReadOnlyList<T> list) => list[rnd.Next(list.Count)];
        decimal Qty() => rnd.Next(4) == 0 ? Math.Round((decimal)rnd.NextDouble() * 3 + 0.1m, 3) : rnd.Next(1, 6);
        decimal Price(decimal min, decimal max) => Math.Round(min + (decimal)rnd.NextDouble() * (max - min), 2);

        var accepted = 0; var rejected = 0;
        for (var step = 0; step < 400; step++)
        {
            var op = rnd.Next(16);
            var name = $"шаг {step}, операция {op}";
            try
            {
                switch (op)
                {
                    case 0 or 1: // закупка, часть оплачена
                    {
                        var lines = Enumerable.Range(0, rnd.Next(1, 4)).Select(_ => new DocumentLine(Pick(products), Qty() + 2, Price(5, 30))).ToList();
                        var total = lines.Sum(l => Money.Round(Money.RoundQty(l.Quantity) * l.Price));
                        var paid = rnd.Next(3) switch { 0 => 0, 1 => total, _ => Money.Round(total / 2) };
                        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(Pick(suppliers), Pick(warehouses), lines, paid, paid > 0 ? Pick(cashes) : null));
                        break;
                    }
                    case 2 or 3 or 4: // продажа: наличные / в долг / смешанная
                    {
                        var lines = Enumerable.Range(0, rnd.Next(1, 3)).Select(_ => new DocumentLine(Pick(products), Qty(), Price(20, 60))).ToList();
                        if (rnd.Next(5) == 0) lines.Add(new DocumentLine(service.Id, 1, 15m));
                        var total = lines.Sum(l => Money.Round(Money.RoundQty(l.Quantity) * Money.Round(l.Price)));
                        var kind = rnd.Next(3);
                        var customer = kind == 0 && rnd.Next(2) == 0 ? (int?)null : Pick(customers);
                        SaleRequest req = kind switch
                        {
                            0 => new(Pick(warehouses), Pick(cashes), lines, customer),
                            1 => new(Pick(warehouses), c1, lines, customer, OnCredit: true,
                                Payments: rnd.Next(2) == 0 ? [] : [new SalePayment(c1, Money.Round(total / 3))]),
                            _ => new(Pick(warehouses), c1, lines, customer,
                                Payments: [new SalePayment(c1, Money.Round(total / 2)), new SalePayment(c2, total - Money.Round(total / 2))])
                        };
                        await t.Sales.CreateSaleAsync(req);
                        break;
                    }
                    case 5: // возврат покупателя
                    {
                        var item = t.Query(db => db.SaleItems.AsNoTracking().Where(i => i.Quantity > i.ReturnedQuantity).ToList()).OrderBy(_ => rnd.Next()).FirstOrDefault();
                        if (item is null) break;
                        var qty = Math.Min(item.Quantity - item.ReturnedQuantity, rnd.Next(2) == 0 ? 1 : item.Quantity);
                        await t.Sales.CreateReturnAsync(new SaleReturnRequest(item.SaleId, Pick(cashes), [new ReturnLine(item.Id, qty)]));
                        break;
                    }
                    case 6: // возврат поставщику
                    {
                        var item = t.Query(db => db.PurchaseItems.AsNoTracking().Where(i => i.Quantity > i.ReturnedQuantity).ToList()).OrderBy(_ => rnd.Next()).FirstOrDefault();
                        if (item is null) break;
                        await t.Purchases.CreateReturnAsync(new PurchaseReturnRequest(item.PurchaseId, [new PurchaseReturnLine(item.Id, Math.Min(1, item.Quantity - item.ReturnedQuantity))], Pick(cashes)));
                        break;
                    }
                    case 7: // платежи контрагентам
                        await t.Finance.CounterpartyPaymentAsync(new CounterpartyPaymentRequest(
                            rnd.Next(2) == 0 ? CashDirection.Expense : CashDirection.Income, Pick(cashes),
                            rnd.Next(2) == 0 ? Pick(suppliers) : Pick(customers), Price(10, 150)));
                        break;
                    case 8: // ручные приход/расход
                        await t.Finance.AddOperationAsync(Pick(cashes), rnd.Next(2) == 0 ? CashDirection.Income : CashDirection.Expense,
                            Pick(new[] { "Аренда", "Прочий доход", ReportService.CashWithdrawal }), Price(5, 100), null);
                        break;
                    case 9: // перемещение денег и товара
                        if (rnd.Next(2) == 0) await t.Finance.TransferAsync(c1, c2, Price(5, 100), null);
                        else await t.Warehouse.TransferAsync(w1, w2, [new TransferLine(Pick(products), Qty())]);
                        break;
                    case 10: // списание
                        await t.Warehouse.WriteOffAsync(Pick(warehouses), [new TransferLine(Pick(products), Math.Min(Qty(), 1))], "Брак");
                        break;
                    case 11: // инвентаризация
                        await t.Warehouse.CreateInventoryAsync(Pick(warehouses), [new InventoryLine(Pick(products), rnd.Next(0, 8))]);
                        break;
                    case 12: // изменение или удаление продажи
                    {
                        var sale = t.Query(db => db.Sales.AsNoTracking().Include(s => s.Items).Where(s => s.ReturnedTotal == 0).ToList()).OrderBy(_ => rnd.Next()).FirstOrDefault();
                        if (sale is null) break;
                        if (rnd.Next(2) == 0) await t.Sales.DeleteSaleAsync(sale.Id);
                        else await t.Sales.UpdateSaleAsync(sale.Id, new SaleRequest(sale.WarehouseId, sale.CashRegisterId,
                            sale.Items.Select(i => new DocumentLine(i.ProductId, Math.Max(0.5m, i.Quantity - 1), i.Price)).ToList(), sale.CustomerId,
                            OnCredit: sale.Total != sale.PaidAmount, Payments: sale.Total != sale.PaidAmount ? [] : null));
                        break;
                    }
                    case 13: // изменение или удаление закупки
                    {
                        var p = t.Query(db => db.Purchases.AsNoTracking().Include(x => x.Items).Where(x => x.ReturnedTotal == 0).ToList()).OrderBy(_ => rnd.Next()).FirstOrDefault();
                        if (p is null) break;
                        if (rnd.Next(2) == 0) await t.Purchases.DeletePurchaseAsync(p.Id);
                        else await t.Purchases.UpdatePurchaseAsync(p.Id, new PurchaseRequest(p.SupplierId, p.WarehouseId,
                            p.Items.Select(i => new DocumentLine(i.ProductId, i.Quantity + 1, i.Price + 1)).ToList()));
                        break;
                    }
                    case 14: // начисление контрагенту
                        await t.Finance.AccrueAsync(rnd.Next(2) == 0 ? CashDirection.Expense : CashDirection.Income,
                            Pick(customers.Concat(suppliers).ToList()), Price(5, 50), "Корректировка", null);
                        break;
                    case 15: // производство
                    {
                        var output = products[0];
                        await t.Production.ProduceAsync(new ProductionRequest(w1, output, rnd.Next(1, 3),
                            [new MaterialLine(products[1], 1), new MaterialLine(products[2], 0.5m)]));
                        break;
                    }
                }
                accepted++;
            }
            catch (BusinessException)
            {
                rejected++; // ожидаемые отказы: нехватка товара/денег, документ с возвратом и т. п.
            }
            await Integrity.AssertConsistentAsync(t, name);
        }

        Assert.True(accepted > 200, $"Слишком мало проведённых операций: {accepted} (отказов {rejected})");
    }

    /// <summary>
    /// Балансовое тождество на операциях, где себестоимость однозначна (без правок/удаления закупок и возвратов поставщику
    /// после частичной продажи партии): деньги + склад по себестоимости + взаиморасчёты
    /// = ручные приходы − ручные расходы + валовая прибыль − потери склада ± начисления.
    /// </summary>
    [Theory]
    [InlineData(11, CostingMethod.Fifo)]
    [InlineData(12, CostingMethod.Average)]
    public async Task Money_stock_and_debts_reconcile_with_results(int seed, CostingMethod method)
    {
        using var t = new TestDb();
        await AccountingIntegrityTests.SetNegativeStockAsync(t, false, method);
        var rnd = new Random(seed);
        var products = new List<int>();
        for (var i = 0; i < 4; i++) products.Add((await t.ProductAsync($"Товар {i}", 30m)).Id);
        var supplier = (await t.SupplierAsync()).Id;
        var customer = (await t.Catalog.SaveAsync(new Counterparty { Name = "Покупатель" })).Id;
        await t.Finance.AddOperationAsync(t.CashId, CashDirection.Income, "Начальный остаток", 2000m, null);
        var ops = 0;

        for (var step = 0; step < 300; step++)
        {
            try
            {
                switch (rnd.Next(8))
                {
                    case 0 or 1:
                        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest(supplier, t.WarehouseId,
                            [new DocumentLine(products[rnd.Next(4)], rnd.Next(1, 10), rnd.Next(5, 25))], 0m));
                        break;
                    case 2 or 3:
                        await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId,
                            [new DocumentLine(products[rnd.Next(4)], rnd.Next(1, 4), rnd.Next(25, 50))], customer, OnCredit: rnd.Next(2) == 0));
                        break;
                    case 4:
                    {
                        var item = t.Query(db => db.SaleItems.AsNoTracking().Where(i => i.Quantity > i.ReturnedQuantity).ToList()).OrderBy(_ => rnd.Next()).FirstOrDefault();
                        if (item is not null)
                            await t.Sales.CreateReturnAsync(new SaleReturnRequest(item.SaleId, t.CashId, [new ReturnLine(item.Id, 1)]));
                        break;
                    }
                    case 5:
                        await t.Finance.CounterpartyPaymentAsync(new CounterpartyPaymentRequest(
                            rnd.Next(2) == 0 ? CashDirection.Expense : CashDirection.Income, t.CashId, rnd.Next(2) == 0 ? supplier : customer, rnd.Next(10, 100)));
                        break;
                    case 6:
                        if (rnd.Next(2) == 0) await t.Warehouse.WriteOffAsync(t.WarehouseId, [new TransferLine(products[rnd.Next(4)], 1)], "Брак");
                        else await t.Warehouse.CreateInventoryAsync(t.WarehouseId, [new InventoryLine(products[rnd.Next(4)], rnd.Next(0, 6))]);
                        break;
                    case 7:
                        await t.Finance.AddOperationAsync(t.CashId, rnd.Next(2) == 0 ? CashDirection.Income : CashDirection.Expense, "Прочее", rnd.Next(5, 60), null);
                        break;
                }
                ops++;
            }
            catch (BusinessException) { }
        }
        await Integrity.AssertConsistentAsync(t);

        using var db = t.CreateDbContext();
        var cash = (await db.CashRegisters.AsNoTracking().ToListAsync()).Sum(c => c.Balance);
        var stock = method == CostingMethod.Fifo
            ? (await db.StockLots.AsNoTracking().Where(l => l.Quantity > 0).ToListAsync()).Sum(l => l.Quantity * l.UnitCost)
            : (await db.StockBalances.AsNoTracking().ToListAsync()).Sum(b => b.Quantity * b.AvgCost);
        var debts = (await new StatementService(t).GetCounterpartyBalancesAsync(DateTime.Today.AddDays(1))).Sum(r => r.Balance);

        var tx = await db.CashTransactions.AsNoTracking().ToListAsync();
        var manual = tx.Where(x => x.Kind == CashOperationKind.Manual).Sum(x => x.SignedAmount);
        var sales = (await db.Sales.AsNoTracking().ToListAsync()).Sum(s => s.Total - s.CostTotal);
        var returns = (await db.SaleReturns.AsNoTracking().ToListAsync()).Sum(r => r.Total - r.CostTotal);
        var writeOffs = (await db.StockWriteOffs.AsNoTracking().ToListAsync()).Sum(w => w.CostTotal);
        var inventory = (await db.InventoryCounts.AsNoTracking().ToListAsync()).Sum(i => i.DifferenceAmount);

        var assets = cash + stock + debts;
        var result = manual + sales - returns - writeOffs + inventory;
        // Допуск — на округление себестоимости единицы (4 знака) по каждой операции
        Assert.True(Math.Abs(assets - result) <= 0.01m * ops,
            $"Деньги {cash} + склад {stock:0.##} + расчёты {debts} = {assets:0.##}, а результат {result:0.##} (операций {ops})");
    }
}
