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
/// Баланс контрагента собирается из движений, так что совпадает с текущим балансом справочника:
/// закупка (−), возврат поставщику (+), оплата поставщику (+), возврат денег поставщиком (−),
/// продажа в долг (+), платежи и начисления контрагенту (выдали +, получили −).
/// </summary>
public class StatementService(IDbContextFactory<TradeDbContext> factory)
{
    private sealed record Move(int CounterpartyId, DateTime Date, string? Document, string Operation, string? Comment, decimal Amount);

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
        var end = EndOf(asOf);
        var sums = (await MovesAsync(db, null, end, ct)).GroupBy(m => m.CounterpartyId).ToDictionary(x => x.Key, x => x.Sum(m => m.Amount));
        return parties.Select(c => new CounterpartyBalanceRow(c.Id, c.Name, c.GroupId, c.Group?.Name, c.Phone, Money.Round(sums.GetValueOrDefault(c.Id))))
            .Where(r => r.Balance != 0 || parties.First(p => p.Id == r.Id).IsActive)
            .ToList();
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
        var moves = await MovesAsync(db, counterpartyId, EndOf(to), ct);
        var opening = moves.Where(m => m.Date < start).Sum(m => m.Amount);
        var rows = moves.Where(m => m.Date >= start).OrderBy(m => m.Date)
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

    /// <summary>Движения взаиморасчётов до момента end (не включая); знак: + контрагент должен нам больше.</summary>
    private static async Task<List<Move>> MovesAsync(TradeDbContext db, int? cpId, DateTime end, CancellationToken ct)
    {
        var moves = new List<Move>();

        var purchases = db.Purchases.AsNoTracking().Where(p => p.Date < end);
        if (cpId is { } a) purchases = purchases.Where(p => p.SupplierId == a);
        foreach (var p in await purchases.Select(p => new { p.SupplierId, p.Date, p.Number, p.Total, p.ReturnedTotal }).ToListAsync(ct))
            moves.Add(new(p.SupplierId, p.Date, p.Number, "Закупка", null, -(p.Total + p.ReturnedTotal)));

        var returns = db.PurchaseReturns.AsNoTracking().Where(r => r.Date < end);
        if (cpId is { } b) returns = returns.Where(r => r.Purchase!.SupplierId == b);
        foreach (var r in await returns.Select(r => new { r.Purchase!.SupplierId, r.Date, r.Number, r.Total, PurchaseNumber = r.Purchase.Number }).ToListAsync(ct))
            moves.Add(new(r.SupplierId, r.Date, r.Number, "Возврат поставщику", r.PurchaseNumber, r.Total));

        var sales = db.Sales.AsNoTracking().Where(s => s.Date < end && s.CustomerId != null && s.Total != s.PaidAmount);
        if (cpId is { } c) sales = sales.Where(s => s.CustomerId == c);
        foreach (var s in await sales.Select(s => new { Id = s.CustomerId!.Value, s.Date, s.Number, s.Total, s.PaidAmount }).ToListAsync(ct))
            moves.Add(new(s.Id, s.Date, s.Number, "Продажа в долг", null, s.Total - s.PaidAmount));

        var kinds = new[] { CashOperationKind.SupplierPayment, CashOperationKind.PurchaseReturn, CashOperationKind.CounterpartyPayment, CashOperationKind.CounterpartyAccrual };
        var ops = db.CashTransactions.AsNoTracking().Where(t => t.Date < end && t.CounterpartyId != null && kinds.Contains(t.Kind));
        if (cpId is { } d) ops = ops.Where(t => t.CounterpartyId == d);
        foreach (var t in await ops.ToListAsync(ct))
            moves.Add(new(t.CounterpartyId!.Value, t.Date, t.DocumentNumber, t.Category, t.Comment,
                t.Direction == CashDirection.Expense ? t.Amount : -t.Amount));

        return moves.OrderBy(m => m.Date).ToList();
    }
}
