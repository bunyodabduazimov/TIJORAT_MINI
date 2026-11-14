using MiniTrade.Core.Common;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Services;

/// <summary>Разрез отчёта о продажах.</summary>
public enum SalesGrouping
{
    Product = 0,
    Category = 1,
    Customer = 2,
    Day = 3,
    Warehouse = 4
}

/// <summary>Строка отчёта о продажах (за вычетом возвратов).</summary>
public record SalesReportRow(string Product, string Unit, decimal Quantity, decimal Revenue, decimal Cost, int Documents = 0)
{
    public decimal Profit => Revenue - Cost;
    public decimal MarginPercent => Revenue == 0 ? 0 : Math.Round(Profit / Revenue * 100, 1);
    /// <summary>Доля в общей выручке, %.</summary>
    public decimal SharePercent { get; init; }
    /// <summary>ABC-класс по выручке (A — 80 %, B — следующие 15 %, C — остальное).</summary>
    public string? Abc { get; init; }
}

public record PurchaseReportRow(string Supplier, int Documents, decimal Total, decimal Paid)
{
    public decimal Debt => Total - Paid;
}

public record ExpenseRow(string Category, decimal Amount)
{
    /// <summary>Доля статьи в общих операционных расходах, %.</summary>
    public decimal Share { get; init; }
}

public record ProfitReport(decimal Revenue, decimal Returns, decimal CostOfSales, decimal Expenses, IReadOnlyList<ExpenseRow> ExpenseItems)
{
    public decimal NetRevenue => Revenue - Returns;
    public decimal GrossProfit => NetRevenue - CostOfSales;
    public decimal NetProfit => GrossProfit - Expenses;
    public int Checks { get; init; }
    public int ReturnsCount { get; init; }
    public decimal AverageCheck => Checks == 0 ? 0 : Money.Round(Revenue / Checks);
    public decimal GrossMarginPercent => NetRevenue == 0 ? 0 : Math.Round(GrossProfit / NetRevenue * 100, 1);
    public decimal NetMarginPercent => NetRevenue == 0 ? 0 : Math.Round(NetProfit / NetRevenue * 100, 1);
}

/// <summary>Движение денег по кассе за период.</summary>
public record CashFlowRow(string CashRegister, decimal Opening, decimal Income, decimal Expense)
{
    public decimal Closing => Opening + Income - Expense;
}

/// <summary>Оборот по статье (виду операции) за период.</summary>
public record CashCategoryRow(CashDirection Direction, string Category, int Operations, decimal Amount)
{
    public bool IsIncome => Direction == CashDirection.Income;
}

public record CashFlowReport(IReadOnlyList<CashFlowRow> Registers, IReadOnlyList<CashCategoryRow> Categories)
{
    public decimal Opening => Registers.Sum(r => r.Opening);
    public decimal Income => Registers.Sum(r => r.Income);
    public decimal Expense => Registers.Sum(r => r.Expense);
    public decimal Closing => Registers.Sum(r => r.Closing);
}

/// <summary>Общий фильтр отчётов. null — без ограничения.</summary>
public sealed record ReportFilter(int? WarehouseId = null, int? UserId = null, int? CounterpartyId = null,
    int? ProductId = null, int? CategoryId = null)
{
    public static readonly ReportFilter None = new();
    /// <summary>Фильтр по строкам товаров (товар или категория).</summary>
    public bool ByItems => ProductId is not null || CategoryId is not null;
}

/// <summary>Пункт справочника для фильтра.</summary>
public sealed record ReportLookup(int Id, string Name);

public sealed record ReportFilterOptions(IReadOnlyList<ReportLookup> Warehouses, IReadOnlyList<ReportLookup> Users,
    IReadOnlyList<ReportLookup> Counterparties, IReadOnlyList<ReportLookup> Products, IReadOnlyList<ReportLookup> Categories);

/// <summary>
/// Отчёты. В SQL выполняются фильтрация и проекция только нужных колонок,
/// суммирование — в памяти (SQLite хранит decimal как TEXT).
/// </summary>
public class ReportService(IDbContextFactory<TradeDbContext> factory)
{
    public const string CashWithdrawal = "Изъятие наличных";
    public const string NoCategory = "Без категории";
    public const string RetailCustomer = "Розничный покупатель";

    private sealed record SaleLine(int SaleId, int ProductId, string Product, string Unit, string? Category,
        int? CustomerId, string? Customer, int WarehouseId, string Warehouse, DateTime Date,
        decimal Quantity, decimal Amount, decimal UnitCost);

    public async Task<List<SalesReportRow>> GetSalesByProductAsync(DateTime from, DateTime to, CancellationToken ct = default)
        => await GetSalesAsync(from, to, SalesGrouping.Product, null, ct);

    /// <summary>Справочники для фильтров отчётов (включая неактивные — по ним могут быть документы).</summary>
    public async Task<ReportFilterOptions> GetFilterOptionsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        static List<ReportLookup> Sort(IEnumerable<ReportLookup> x) =>
            x.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var warehouses = await db.Warehouses.AsNoTracking().Select(x => new ReportLookup(x.Id, x.Name)).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Select(x => new ReportLookup(x.Id, x.FullName != "" ? x.FullName : x.Login)).ToListAsync(ct);
        var counterparties = await db.Counterparties.AsNoTracking().Select(x => new ReportLookup(x.Id, x.Name)).ToListAsync(ct);
        var products = await db.Products.AsNoTracking().Select(x => new ReportLookup(x.Id, x.Name)).ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().Select(x => new ReportLookup(x.Id, x.Name)).ToListAsync(ct);
        return new ReportFilterOptions(Sort(warehouses), Sort(users), Sort(counterparties), Sort(products), Sort(categories));
    }

    private static IQueryable<SaleItem> Apply(IQueryable<SaleItem> q, ReportFilter f)
    {
        if (f.WarehouseId is { } w) q = q.Where(i => i.Sale!.WarehouseId == w);
        if (f.UserId is { } u) q = q.Where(i => i.Sale!.UserId == u);
        if (f.CounterpartyId is { } c) q = q.Where(i => i.Sale!.CustomerId == c);
        if (f.ProductId is { } p) q = q.Where(i => i.ProductId == p);
        if (f.CategoryId is { } k) q = q.Where(i => i.Product!.CategoryId == k);
        return q;
    }

    /// <summary>Возврат: склад и покупатель — от исходной продажи, пользователь — оформивший возврат.</summary>
    private static IQueryable<SaleReturnItem> Apply(IQueryable<SaleReturnItem> q, ReportFilter f)
    {
        if (f.WarehouseId is { } w) q = q.Where(i => i.SaleReturn!.Sale!.WarehouseId == w);
        if (f.UserId is { } u) q = q.Where(i => i.SaleReturn!.UserId == u);
        if (f.CounterpartyId is { } c) q = q.Where(i => i.SaleReturn!.Sale!.CustomerId == c);
        if (f.ProductId is { } p) q = q.Where(i => i.ProductId == p);
        if (f.CategoryId is { } k) q = q.Where(i => i.Product!.CategoryId == k);
        return q;
    }

    /// <summary>Продажи минус возвраты в выбранном разрезе, с долей и ABC-классом.</summary>
    public async Task<List<SalesReportRow>> GetSalesAsync(DateTime from, DateTime to, SalesGrouping grouping,
        ReportFilter? filter = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (f, t) = Range(from, to);
        filter ??= ReportFilter.None;

        var itemsQuery = Apply(db.SaleItems.AsNoTracking().Where(i => i.Sale!.Date >= f && i.Sale.Date < t), filter);
        var returnsQuery = Apply(db.SaleReturnItems.AsNoTracking().Where(i => i.SaleReturn!.Date >= f && i.SaleReturn.Date < t), filter);

        var items = await itemsQuery.Select(i => new SaleLine(i.SaleId, i.ProductId, i.Product!.Name, i.Product.Unit,
                i.Product.Category != null ? i.Product.Category.Name : null,
                i.Sale!.CustomerId, i.Sale.Customer != null ? i.Sale.Customer.Name : null,
                i.Sale.WarehouseId, i.Sale.Warehouse!.Name, i.Sale.Date,
                i.Quantity, i.Amount, i.UnitCost))
            .ToListAsync(ct);
        // Возврат относится к периоду, в котором он оформлен, а реквизиты (покупатель, склад) — от исходной продажи
        var returns = await returnsQuery.Select(i => new SaleLine(-1, i.ProductId, i.Product!.Name, i.Product.Unit,
                i.Product.Category != null ? i.Product.Category.Name : null,
                i.SaleReturn!.Sale!.CustomerId, i.SaleReturn.Sale.Customer != null ? i.SaleReturn.Sale.Customer.Name : null,
                i.SaleReturn.Sale.WarehouseId, i.SaleReturn.Sale.Warehouse!.Name, i.SaleReturn.Date,
                -i.Quantity, -i.Amount, i.UnitCost))
            .ToListAsync(ct);

        var lines = items.Concat(returns);

        IEnumerable<(object Key, string Name, string Unit, IEnumerable<SaleLine> Lines)> groups = grouping switch
        {
            SalesGrouping.Category => lines.GroupBy(l => l.Category ?? NoCategory).Select(g => ((object)g.Key, g.Key, "", g.AsEnumerable())),
            SalesGrouping.Customer => lines.GroupBy(l => l.CustomerId ?? 0).Select(g => ((object)g.Key, g.First().Customer ?? RetailCustomer, "", g.AsEnumerable())),
            SalesGrouping.Day => lines.GroupBy(l => l.Date.Date).OrderBy(g => g.Key).Select(g => ((object)g.Key, g.Key.ToString("dd.MM.yyyy, ddd"), "", g.AsEnumerable())),
            SalesGrouping.Warehouse => lines.GroupBy(l => l.WarehouseId).Select(g => ((object)g.Key, g.First().Warehouse, "", g.AsEnumerable())),
            _ => lines.GroupBy(l => l.ProductId).Select(g => ((object)g.Key, g.First().Product, g.First().Unit, g.AsEnumerable()))
        };

        var rows = groups.Select(g =>
        {
            var list = g.Lines.ToList();
            return new SalesReportRow(g.Name, g.Unit,
                // Количество в разрезах кроме товарного складывает разные единицы — показываем только для товаров
                grouping == SalesGrouping.Product ? list.Sum(x => x.Quantity) : 0,
                Money.Round(list.Sum(x => x.Amount)),
                Money.Round(list.Sum(x => x.Quantity * x.UnitCost)),
                list.Where(x => x.SaleId > 0).Select(x => x.SaleId).Distinct().Count());
        }).ToList();

        if (grouping != SalesGrouping.Day) rows = rows.OrderByDescending(r => r.Revenue).ToList();
        return WithShares(rows, abc: grouping is SalesGrouping.Product or SalesGrouping.Category or SalesGrouping.Customer);
    }

    private static List<SalesReportRow> WithShares(List<SalesReportRow> rows, bool abc)
    {
        var total = rows.Where(r => r.Revenue > 0).Sum(r => r.Revenue);
        if (total <= 0) return rows;
        decimal cumulative = 0;
        var result = new List<SalesReportRow>(rows.Count);
        foreach (var r in rows)
        {
            var share = r.Revenue > 0 ? Math.Round(r.Revenue / total * 100, 1) : 0;
            string? cls = null;
            if (abc)
            {
                // Класс определяется по накопленной доле до добавления позиции: первая позиция всегда A
                var before = cumulative / total;
                cls = r.Revenue <= 0 ? "C" : before < 0.80m ? "A" : before < 0.95m ? "B" : "C";
                cumulative += Math.Max(r.Revenue, 0);
            }
            result.Add(r with { SharePercent = share, Abc = cls });
        }
        return result;
    }

    public async Task<List<PurchaseReportRow>> GetPurchasesBySupplierAsync(DateTime from, DateTime to, ReportFilter? filter = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (f, t) = Range(from, to);
        filter ??= ReportFilter.None;
        var query = db.Purchases.AsNoTracking().Where(p => p.Date >= f && p.Date < t);
        if (filter.WarehouseId is { } w) query = query.Where(p => p.WarehouseId == w);
        if (filter.UserId is { } u) query = query.Where(p => p.UserId == u);
        if (filter.CounterpartyId is { } c) query = query.Where(p => p.SupplierId == c);

        if (!filter.ByItems)
        {
            var list = await query.Select(p => new { p.SupplierId, Supplier = p.Supplier!.Name, p.Total, p.PaidAmount }).ToListAsync(ct);
            // Группируем по Id: у разных поставщиков может быть одинаковое название
            return list.GroupBy(p => p.SupplierId)
                .Select(g => new PurchaseReportRow(g.First().Supplier, g.Count(), g.Sum(x => x.Total), g.Sum(x => x.PaidAmount)))
                .OrderByDescending(r => r.Total).ToList();
        }

        // По товару/категории: сумма — только подходящие строки (за вычетом возвращённого),
        // оплата распределяется пропорционально доле этих строк в документе
        var ids = query.Select(p => p.Id);
        var items = db.PurchaseItems.AsNoTracking().Where(i => ids.Contains(i.PurchaseId));
        if (filter.ProductId is { } pr) items = items.Where(i => i.ProductId == pr);
        if (filter.CategoryId is { } k) items = items.Where(i => i.Product!.CategoryId == k);
        var lines = await items.Select(i => new
            {
                i.PurchaseId, i.Purchase!.SupplierId, Supplier = i.Purchase.Supplier!.Name, i.Purchase.Total, i.Purchase.PaidAmount,
                i.Quantity, i.ReturnedQuantity, i.Amount
            }).ToListAsync(ct);
        return lines.GroupBy(l => l.SupplierId)
            .Select(g =>
            {
                var docs = g.GroupBy(x => x.PurchaseId).Select(d =>
                {
                    var first = d.First();
                    var amount = d.Sum(x => x.Quantity == 0 ? x.Amount : Money.Round(x.Amount * (x.Quantity - x.ReturnedQuantity) / x.Quantity));
                    var paid = first.Total == 0 ? 0 : Money.Round(Math.Min(first.PaidAmount, first.Total) * amount / first.Total);
                    return (Amount: amount, Paid: paid);
                }).ToList();
                return new PurchaseReportRow(g.First().Supplier, docs.Count, docs.Sum(d => d.Amount), docs.Sum(d => d.Paid));
            })
            .OrderByDescending(r => r.Total).ToList();
    }

    public async Task<ProfitReport> GetProfitAsync(DateTime from, DateTime to, ReportFilter? filter = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (f, t) = Range(from, to);
        filter ??= ReportFilter.None;

        decimal revenue, returnsTotal, cost;
        int checks, returnsCount;
        if (filter.ByItems)
        {
            // По товару/категории считаем по строкам документов
            var items = await Apply(db.SaleItems.AsNoTracking().Where(i => i.Sale!.Date >= f && i.Sale.Date < t), filter)
                .Select(i => new { i.SaleId, i.Amount, i.Quantity, i.UnitCost }).ToListAsync(ct);
            var ret = await Apply(db.SaleReturnItems.AsNoTracking().Where(i => i.SaleReturn!.Date >= f && i.SaleReturn.Date < t), filter)
                .Select(i => new { i.SaleReturnId, i.Amount, i.Quantity, i.UnitCost }).ToListAsync(ct);
            revenue = items.Sum(i => i.Amount);
            returnsTotal = ret.Sum(i => i.Amount);
            cost = Money.Round(items.Sum(i => i.Quantity * i.UnitCost) - ret.Sum(i => i.Quantity * i.UnitCost));
            checks = items.Select(i => i.SaleId).Distinct().Count();
            returnsCount = ret.Select(i => i.SaleReturnId).Distinct().Count();
        }
        else
        {
            var salesQ = db.Sales.AsNoTracking().Where(s => s.Date >= f && s.Date < t);
            var returnsQ = db.SaleReturns.AsNoTracking().Where(r => r.Date >= f && r.Date < t);
            if (filter.WarehouseId is { } w) { salesQ = salesQ.Where(s => s.WarehouseId == w); returnsQ = returnsQ.Where(r => r.Sale!.WarehouseId == w); }
            if (filter.UserId is { } u) { salesQ = salesQ.Where(s => s.UserId == u); returnsQ = returnsQ.Where(r => r.UserId == u); }
            if (filter.CounterpartyId is { } c) { salesQ = salesQ.Where(s => s.CustomerId == c); returnsQ = returnsQ.Where(r => r.Sale!.CustomerId == c); }
            var sales = await salesQ.Select(s => new { s.Total, s.CostTotal }).ToListAsync(ct);
            var returns = await returnsQ.Select(r => new { r.Total, r.CostTotal }).ToListAsync(ct);
            revenue = sales.Sum(s => s.Total);
            returnsTotal = returns.Sum(r => r.Total);
            cost = sales.Sum(s => s.CostTotal) - returns.Sum(r => r.CostTotal);
            checks = sales.Count;
            returnsCount = returns.Count;
        }

        // Расходы не привязаны к складу и товарам: при таком фильтре они не учитываются
        var expenseRows = new List<ExpenseRow>();
        // Потери склада (списания, недостачи/излишки инвентаризации) привязаны к складу, но не к пользователю/контрагенту
        if (!filter.ByItems && filter.UserId is null && filter.CounterpartyId is null)
            expenseRows.AddRange(await StockLossesAsync(db, f, t, filter.WarehouseId, ct));
        if (filter.WarehouseId is null && !filter.ByItems)
        {
            var expQ = db.CashTransactions.AsNoTracking()
                .Where(x => x.Date >= f && x.Date < t && x.Kind == CashOperationKind.Manual && x.Direction == CashDirection.Expense
                            && x.Category != CashWithdrawal); // Изъятие наличных — не расход бизнеса
            if (filter.UserId is { } u) expQ = expQ.Where(x => x.UserId == u);
            if (filter.CounterpartyId is { } c) expQ = expQ.Where(x => x.CounterpartyId == c);
            var expenses = await expQ.Select(x => new { x.Category, x.Amount }).ToListAsync(ct);
            expenseRows.AddRange(expenses.GroupBy(e => e.Category).Select(g => new ExpenseRow(g.Key, g.Sum(x => x.Amount))));
        }
        expenseRows = expenseRows.OrderByDescending(r => r.Amount).ToList();
        var totalExp = expenseRows.Where(r => r.Amount > 0).Sum(r => r.Amount);
        if (totalExp != 0)
            expenseRows = expenseRows.Select(r => r with { Share = Math.Round(r.Amount / totalExp * 100, 1) }).ToList();

        return new ProfitReport(
            Revenue: revenue,
            Returns: returnsTotal,
            CostOfSales: cost,
            Expenses: expenseRows.Sum(r => r.Amount),
            ExpenseItems: expenseRows)
        {
            Checks = checks,
            ReturnsCount = returnsCount
        };
    }

    /// <summary>
    /// Движение денег: остаток на начало, приход, расход, остаток на конец по каждой кассе и обороты по статьям.
    /// Остаток на начало восстанавливается от текущего остатка кассы по журналу операций.
    /// </summary>
    public async Task<CashFlowReport> GetCashFlowAsync(DateTime from, DateTime to, ReportFilter? filter = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (f, t) = Range(from, to);
        filter ??= ReportFilter.None;
        var registers = await db.CashRegisters.AsNoTracking().Where(r => !r.IsDeleted)
            .Select(r => new { r.Id, r.Name, r.Balance }).ToListAsync(ct);
        var txQ = db.CashTransactions.AsNoTracking().Where(x => x.Date >= f && x.Kind != CashOperationKind.CounterpartyAccrual);
        // Касса не привязана к складу и товарам — действуют только пользователь и контрагент.
        // С таким фильтром остаток кассы не восстановить: показываются только обороты за период.
        var partial = filter.UserId is not null || filter.CounterpartyId is not null;
        if (filter.UserId is { } u) txQ = txQ.Where(x => x.UserId == u);
        if (filter.CounterpartyId is { } c) txQ = txQ.Where(x => x.CounterpartyId == c);
        var tx = await txQ.Select(x => new { x.CashRegisterId, x.Date, x.Direction, x.Kind, x.Category, x.Amount }).ToListAsync(ct);

        var rows = new List<CashFlowRow>();
        foreach (var r in registers.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var mine = tx.Where(x => x.CashRegisterId == r.Id).ToList();
            var afterFrom = mine.Sum(x => x.Direction == CashDirection.Income ? x.Amount : -x.Amount);
            var inPeriod = mine.Where(x => x.Date < t).ToList();
            var income = inPeriod.Where(x => x.Direction == CashDirection.Income).Sum(x => x.Amount);
            var expense = inPeriod.Where(x => x.Direction == CashDirection.Expense).Sum(x => x.Amount);
            if (partial)
            {
                if (inPeriod.Count > 0) rows.Add(new CashFlowRow(r.Name, 0, income, expense));
                continue;
            }
            if (afterFrom == 0 && r.Balance == 0 && inPeriod.Count == 0) continue;
            rows.Add(new CashFlowRow(r.Name, r.Balance - afterFrom, income, expense));
        }

        var categories = tx.Where(x => x.Date < t)
            .GroupBy(x => (x.Direction, Name: CategoryName(x.Kind, x.Category)))
            .Select(g => new CashCategoryRow(g.Key.Direction, g.Key.Name, g.Count(), g.Sum(x => x.Amount)))
            .OrderBy(c => c.Direction).ThenByDescending(c => c.Amount)
            .ToList();

        return new CashFlowReport(rows, categories);
    }

    public const string WriteOffArticle = "Списание товара";
    public const string ShortageArticle = "Недостача по инвентаризации";
    public const string SurplusArticle = "Излишки по инвентаризации";

    /// <summary>
    /// Потери склада за период по себестоимости — строки расходов для прибыли: списания, недостачи
    /// и излишки инвентаризации (излишек — отрицательный расход, т. е. доход).
    /// </summary>
    internal static async Task<List<ExpenseRow>> StockLossesAsync(TradeDbContext db, DateTime from, DateTime to, int? warehouseId, CancellationToken ct)
    {
        var writeOffs = db.StockWriteOffs.AsNoTracking().Where(w => w.Date >= from && w.Date < to);
        var counts = db.Set<InventoryCountItem>().AsNoTracking()
            .Where(i => db.InventoryCounts.Any(c => c.Id == i.InventoryCountId && c.Date >= from && c.Date < to
                                                    && (warehouseId == null || c.WarehouseId == warehouseId)));
        if (warehouseId is { } w) writeOffs = writeOffs.Where(x => x.WarehouseId == w);
        var writeOff = (await writeOffs.Select(x => x.CostTotal).ToListAsync(ct)).Sum();
        var diffs = (await counts.Select(i => new { i.Difference, i.UnitCost }).ToListAsync(ct))
            .Select(i => Money.Round(i.Difference * i.UnitCost)).ToList();
        var shortage = -diffs.Where(d => d < 0).Sum();
        var surplus = diffs.Where(d => d > 0).Sum();

        var rows = new List<ExpenseRow>();
        if (writeOff != 0) rows.Add(new ExpenseRow(WriteOffArticle, Money.Round(writeOff)));
        if (shortage != 0) rows.Add(new ExpenseRow(ShortageArticle, Money.Round(shortage)));
        if (surplus != 0) rows.Add(new ExpenseRow(SurplusArticle, -Money.Round(surplus)));
        return rows;
    }

    private static string CategoryName(CashOperationKind kind, string category) =>
        !string.IsNullOrWhiteSpace(category) ? category : kind switch
        {
            CashOperationKind.Sale => "Продажа",
            CashOperationKind.SaleReturn => "Возврат покупателю",
            CashOperationKind.SupplierPayment => "Оплата поставщику",
            CashOperationKind.PurchaseReturn => "Возврат от поставщика",
            CashOperationKind.TransferIn or CashOperationKind.TransferOut => "Перемещение между кассами",
            CashOperationKind.CounterpartyPayment => "Расчёты с контрагентами",
            CashOperationKind.CounterpartyAccrual => "Начисление контрагенту",
            _ => "Прочее"
        };

    private static (DateTime From, DateTime To) Range(DateTime from, DateTime to)
    {
        if (from > to) (from, to) = (to, from);
        return (from.Date, to.Date.AddDays(1));
    }
}
