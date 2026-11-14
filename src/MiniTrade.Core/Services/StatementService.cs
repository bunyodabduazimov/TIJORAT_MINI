using MiniTrade.Core.Common;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Services;

/// <summary>Строка выписки: приход (увеличение) и расход (уменьшение), остаток после операции.</summary>
public sealed record StatementRow(DateTime Date, string? Document, string Operation, string? Comment, decimal Income, decimal Expense)
{
    public decimal Balance { get; init; }
}

/// <summary>Выписка за период: начальный остаток, движения, обороты и конечный остаток.</summary>
public sealed record Statement(decimal Opening, IReadOnlyList<StatementRow> Rows, decimal Income, decimal Expense, decimal Closing);

/// <summary>Баланс контрагента на дату (+ должен нам, − мы должны).</summary>
public sealed record CounterpartyBalanceRow(int Id, string Name, int? GroupId, string? Group, string? Phone, decimal Balance)
{
    public int BalanceSign => Math.Sign(Balance);
}

/// <summary>Остаток кассы на дату.</summary>
public sealed record CashBalanceRow(int Id, string Name, decimal Balance);

/// <summary>
/// Остатки касс и балансы контрагентов на конец дня, выписки по кассе и контрагенту за период.
/// Балансы контрагентов считает <see cref="CounterpartyLedger"/> — тот же расчёт, что в справочнике и на главной.
/// </summary>
public class StatementService(IDbContextFactory<TradeDbContext> factory)
{
    private static DateTime EndOf(DateTime day) => day.Date.AddDays(1);

    public async Task<List<CashBalanceRow>> GetCashBalancesAsync(DateTime asOf, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var end = EndOf(asOf);
        var cash = await db.CashRegisters.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var ops = await db.CashTransactions.AsNoTracking().Where(t => t.Date < end && t.CashRegisterId != null)
            .Select(t => new { Id = t.CashRegisterId!.Value, t.Direction, t.Amount }).ToListAsync(ct);
        var sums = ops.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.Sum(o => o.Direction == CashDirection.Income ? o.Amount : -o.Amount));
        return cash.Select(c => new CashBalanceRow(c.Id, c.Name, Money.Round(sums.GetValueOrDefault(c.Id)))).ToList();
    }

    public async Task<List<CounterpartyBalanceRow>> GetCounterpartyBalancesAsync(DateTime asOf, int? groupId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Counterparties.AsNoTracking().Include(c => c.Group).Where(c => !c.IsDeleted);
        if (groupId is { } g) q = q.Where(c => c.GroupId == g);
        var parties = await q.OrderBy(c => c.Name).ToListAsync(ct);
        var sums = await CounterpartyLedger.BalancesAsync(db, null, EndOf(asOf), ct);
        return parties.Select(c => new CounterpartyBalanceRow(c.Id, c.Name, c.GroupId, c.Group?.Name, c.Phone, sums.GetValueOrDefault(c.Id)))
            .Where(r => r.Balance != 0 || parties.First(p => p.Id == r.Id).IsActive)
            .ToList();
    }

    /// <summary>Текущий баланс одного контрагента (+ должен нам, − мы должны).</summary>
    public async Task<decimal> GetCounterpartyBalanceAsync(int counterpartyId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await CounterpartyLedger.BalancesAsync(db, [counterpartyId], null, ct)).GetValueOrDefault(counterpartyId);
    }

    public async Task<Statement> GetCashStatementAsync(int cashRegisterId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var start = from.Date; var end = EndOf(to);
        var ops = await db.CashTransactions.AsNoTracking().Include(t => t.Counterparty)
            .Where(t => t.CashRegisterId == cashRegisterId && t.Date < end).ToListAsync(ct);
        var opening = ops.Where(o => o.Date < start).Sum(o => o.SignedAmount);
        var rows = ops.Where(o => o.Date >= start).OrderBy(o => o.Date).ThenBy(o => o.Id)
            .Select(o => new StatementRow(o.Date, o.DocumentNumber, o.Category,
                string.Join(" · ", new[] { o.CounterpartyName, o.Comment }.Where(s => !string.IsNullOrWhiteSpace(s))),
                o.Direction == CashDirection.Income ? o.Amount : 0, o.Direction == CashDirection.Expense ? o.Amount : 0));
        return Build(opening, rows);
    }

    public async Task<Statement> GetCounterpartyStatementAsync(int counterpartyId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var start = from.Date;
        var moves = await CounterpartyLedger.MovesAsync(db, [counterpartyId], EndOf(to), ct);
        var opening = moves.Where(m => m.Date < start).Sum(m => m.Amount);
        var rows = moves.Where(m => m.Date >= start)
            .Select(m => new StatementRow(m.Date, m.Document, m.Operation, m.Comment, m.Amount > 0 ? m.Amount : 0, m.Amount < 0 ? -m.Amount : 0));
        return Build(opening, rows);
    }

    private static Statement Build(decimal opening, IEnumerable<StatementRow> source)
    {
        var balance = Money.Round(opening);
        var rows = new List<StatementRow>();
        foreach (var r in source)
        {
            balance = Money.Round(balance + r.Income - r.Expense);
            rows.Add(r with { Balance = balance });
        }
        return new Statement(Money.Round(opening), rows, Money.Round(rows.Sum(r => r.Income)), Money.Round(rows.Sum(r => r.Expense)), balance);
    }
}

/// <summary>
/// Регистр взаиморасчётов с контрагентами — единственный источник балансов (справочник, выписки, главная, долги).
/// Знак: + контрагент должен нам, − мы должны контрагенту.
/// <list type="bullet">
/// <item>закупка −(сумма до возвратов), возврат поставщику +сумма;</item>
/// <item>продажа покупателю в долг +неоплаченная часть, возврат от покупателя −сумма возврата;</item>
/// <item>деньги контрагенту (оплата поставщику, выплата покупателю при возврате, платёж/начисление «выдали») +;</item>
/// <item>деньги от контрагента (возврат денег поставщиком, платёж/начисление «получили») −.</item>
/// </list>
/// </summary>
internal static class CounterpartyLedger
{
    public sealed record Move(int CounterpartyId, DateTime Date, string? Document, string Operation, string? Comment, decimal Amount);

    /// <summary>Кассовые операции, которые меняют взаиморасчёты (при указанном контрагенте).</summary>
    private static readonly CashOperationKind[] Kinds =
    [
        CashOperationKind.SupplierPayment, CashOperationKind.PurchaseReturn, CashOperationKind.CounterpartyPayment,
        CashOperationKind.CounterpartyAccrual, CashOperationKind.SaleReturn
    ];

    /// <summary>Балансы контрагентов (ids = null — всех) на момент end (не включая; null — текущие).</summary>
    public static async Task<Dictionary<int, decimal>> BalancesAsync(TradeDbContext db, IReadOnlyCollection<int>? ids, DateTime? end, CancellationToken ct) =>
        (await MovesAsync(db, ids, end, ct)).GroupBy(m => m.CounterpartyId)
            .ToDictionary(g => g.Key, g => Money.Round(g.Sum(m => m.Amount)));

    /// <summary>Движения взаиморасчётов в хронологическом порядке.</summary>
    public static async Task<List<Move>> MovesAsync(TradeDbContext db, IReadOnlyCollection<int>? ids, DateTime? end, CancellationToken ct)
    {
        var until = end ?? new DateTime(3000, 1, 1); // без ограничения (DateTime.MaxValue не все СУБД принимают)
        var moves = new List<Move>();

        var purchases = db.Purchases.AsNoTracking().Where(p => p.Date < until);
        if (ids is not null) purchases = purchases.Where(p => ids.Contains(p.SupplierId));
        foreach (var p in await purchases.Select(p => new { p.SupplierId, p.Date, p.Number, p.Total, p.ReturnedTotal }).ToListAsync(ct))
            moves.Add(new(p.SupplierId, p.Date, p.Number, "Закупка", null, -(p.Total + p.ReturnedTotal)));

        var purchaseReturns = db.PurchaseReturns.AsNoTracking().Where(r => r.Date < until);
        if (ids is not null) purchaseReturns = purchaseReturns.Where(r => ids.Contains(r.Purchase!.SupplierId));
        foreach (var r in await purchaseReturns.Select(r => new { r.Purchase!.SupplierId, r.Date, r.Number, r.Total, PurchaseNumber = r.Purchase.Number }).ToListAsync(ct))
            moves.Add(new(r.SupplierId, r.Date, r.Number, "Возврат поставщику", r.PurchaseNumber, r.Total));

        var sales = db.Sales.AsNoTracking().Where(s => s.Date < until && s.CustomerId != null && s.Total != s.PaidAmount);
        if (ids is not null) sales = sales.Where(s => ids.Contains(s.CustomerId!.Value));
        foreach (var s in await sales.Select(s => new { Id = s.CustomerId!.Value, s.Date, s.Number, s.Total, s.PaidAmount }).ToListAsync(ct))
            moves.Add(new(s.Id, s.Date, s.Number, "Продажа в долг", null, s.Total - s.PaidAmount));

        // Возврат покупателем: уменьшает его долг; выданные ему деньги — отдельная кассовая операция ниже
        var saleReturns = db.SaleReturns.AsNoTracking().Where(r => r.Date < until && r.Sale!.CustomerId != null);
        if (ids is not null) saleReturns = saleReturns.Where(r => ids.Contains(r.Sale!.CustomerId!.Value));
        foreach (var r in await saleReturns.Select(r => new { Id = r.Sale!.CustomerId!.Value, r.Date, r.Number, r.Total, SaleNumber = r.Sale.Number }).ToListAsync(ct))
            moves.Add(new(r.Id, r.Date, r.Number, "Возврат от покупателя", r.SaleNumber, -r.Total));

        var ops = db.CashTransactions.AsNoTracking().Where(t => t.Date < until && t.CounterpartyId != null && Kinds.Contains(t.Kind));
        if (ids is not null) ops = ops.Where(t => ids.Contains(t.CounterpartyId!.Value));
        foreach (var t in await ops.Select(t => new { t.CounterpartyId, t.Date, t.DocumentNumber, t.Category, t.Comment, t.Direction, t.Amount, t.Id }).ToListAsync(ct))
            moves.Add(new(t.CounterpartyId!.Value, t.Date, t.DocumentNumber, t.Category, t.Comment,
                t.Direction == CashDirection.Expense ? t.Amount : -t.Amount));

        return moves.OrderBy(m => m.Date).ToList();
    }

    /// <summary>Долги по сторонам: сколько должны нам (сумма положительных балансов) и сколько должны мы.</summary>
    public static (decimal Receivable, decimal Payable) Totals(IEnumerable<decimal> balances)
    {
        decimal receivable = 0, payable = 0;
        foreach (var b in balances)
            if (b > 0) receivable += b; else payable -= b;
        return (Money.Round(receivable), Money.Round(payable));
    }
}
