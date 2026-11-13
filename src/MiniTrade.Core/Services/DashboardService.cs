using MiniTrade.Core.Common;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Services;

public record DailyPoint(DateTime Date, decimal Amount);

public record TopProductRow(string Product, string Unit, decimal Quantity, decimal Revenue);

public record DashboardSummary(
    decimal TodayRevenue, int TodaySalesCount, decimal YesterdayRevenue,
    decimal MonthRevenue, int MonthSalesCount, decimal MonthProfit, decimal MonthExpenses,
    decimal CashTotal, decimal StockCostValue, decimal StockSaleValue, decimal SupplierDebt, int ProductsCount,
    IReadOnlyList<DailyPoint> Last7Days, int LowStockCount, IReadOnlyList<StockRow> LowStock,
    IReadOnlyList<TopProductRow> TopProducts, IReadOnlyList<Sale> RecentSales)
{
    /// <summary>Средний чек за месяц.</summary>
    public decimal MonthAverageCheck => MonthSalesCount == 0 ? 0 : Money.Round(MonthRevenue / MonthSalesCount);
    public decimal WeekRevenue => Last7Days.Sum(d => d.Amount);
    /// <summary>Наценка склада: розничная стоимость минус себестоимость.</summary>
    public decimal StockMarkup => StockSaleValue - StockCostValue;
    /// <summary>Изменение выручки к вчерашнему дню, %; null — вчера продаж не было.</summary>
    public decimal? TodayChangePercent => YesterdayRevenue == 0 ? null : Math.Round((TodayRevenue - YesterdayRevenue) / YesterdayRevenue * 100, 1);
}

/// <summary>
/// Сводка для главной страницы — один контекст БД и только нужные колонки.
/// Суммы считаются в памяти: SQLite хранит decimal как TEXT.
/// </summary>
public class DashboardService(IDbContextFactory<TradeDbContext> factory)
{
    private const string CashWithdrawal = "Изъятие наличных"; // не расход бизнеса (как в ReportService.GetProfitAsync)

    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var weekStart = today.AddDays(-6);
        var from = weekStart < monthStart ? weekStart : monthStart;

        await using var db = await factory.CreateDbContextAsync(ct);

        var sales = await db.Sales.AsNoTracking().Where(s => s.Date >= from)
            .Select(s => new { s.Date, s.Total, s.CostTotal }).ToListAsync(ct);
        var returns = await db.SaleReturns.AsNoTracking().Where(r => r.Date >= from)
            .Select(r => new { r.Date, r.Total, r.CostTotal }).ToListAsync(ct);

        decimal Revenue(DateTime start, DateTime end) =>
            sales.Where(s => s.Date >= start && s.Date < end).Sum(s => s.Total)
            - returns.Where(r => r.Date >= start && r.Date < end).Sum(r => r.Total);

        var tomorrow = today.AddDays(1);
        var monthSales = sales.Where(s => s.Date >= monthStart).ToList();
        var monthReturns = returns.Where(r => r.Date >= monthStart).ToList();

        var expenses = (await db.CashTransactions.AsNoTracking()
                .Where(x => x.Date >= monthStart && x.Kind == CashOperationKind.Manual
                            && x.Direction == CashDirection.Expense && x.Category != CashWithdrawal)
                .Select(x => x.Amount).ToListAsync(ct)).Sum();
        var profit = new ProfitReport(monthSales.Sum(s => s.Total), monthReturns.Sum(r => r.Total),
            monthSales.Sum(s => s.CostTotal) - monthReturns.Sum(r => r.CostTotal), expenses, []);

        var days = Enumerable.Range(0, 7).Select(i => weekStart.AddDays(i))
            .Select(d => new DailyPoint(d, Revenue(d, d.AddDays(1)))).ToList();

        var cash = (await db.CashRegisters.AsNoTracking().Select(c => c.Balance).ToListAsync(ct)).Sum();
        var supplierDebt = (await db.Purchases.AsNoTracking().Select(p => new { p.Total, p.PaidAmount }).ToListAsync(ct))
            .Sum(p => Math.Max(0, p.Total - p.PaidAmount));
        var products = await db.Products.CountAsync(p => p.IsActive, ct);

        // Остатки: только нужные поля, без загрузки категорий
        var stock = await db.StockBalances.AsNoTracking()
            .Select(b => new StockRow(b.ProductId, b.Product!.Name, b.Product.Sku, b.Product.Unit, null,
                b.WarehouseId, b.Warehouse!.Name, b.Quantity, b.AvgCost, b.Product.SalePrice, b.Product.MinStock))
            .ToListAsync(ct);
        // Нулевой остаток тоже «мало» — его показываем первым
        var low = stock.Where(s => s.IsLow).OrderBy(s => s.MinStock == 0 ? 1 : s.Quantity / s.MinStock).ThenBy(s => s.Product).ToList();

        var top = (await db.SaleItems.AsNoTracking().Where(i => i.Sale!.Date >= monthStart)
                .Select(i => new { i.ProductId, i.Product!.Name, i.Product.Unit, i.Quantity, i.ReturnedQuantity, i.Amount, i.Price })
                .ToListAsync(ct))
            .GroupBy(i => (i.ProductId, i.Name, i.Unit))
            .Select(g => new TopProductRow(g.Key.Name, g.Key.Unit,
                g.Sum(i => i.Quantity - i.ReturnedQuantity),
                Money.Round(g.Sum(i => i.Amount - i.ReturnedQuantity * i.Price))))
            .Where(r => r.Revenue > 0)
            .OrderByDescending(r => r.Revenue).Take(5).ToList();

        var recent = await db.Sales.AsNoTracking().Include(s => s.Customer)
            .OrderByDescending(s => s.Id).Take(8).ToListAsync(ct);

        return new DashboardSummary(
            Revenue(today, tomorrow), sales.Count(s => s.Date >= today), Revenue(yesterday, today),
            Revenue(monthStart, tomorrow), monthSales.Count, profit.NetProfit, expenses,
            cash, stock.Where(s => s.Quantity > 0).Sum(s => s.CostValue), stock.Where(s => s.Quantity > 0).Sum(s => s.SaleValue),
            supplierDebt, products, days, low.Count, low.Take(10).ToList(), top, recent);
    }
}
