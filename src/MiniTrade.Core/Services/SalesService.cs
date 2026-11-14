using MiniTrade.Core.Common;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Services;

public record DocumentLine(int ProductId, decimal Quantity, decimal Price);

public record SaleRequest(int WarehouseId, int CashRegisterId, IReadOnlyList<DocumentLine> Lines,
    int? CustomerId = null, string? Comment = null, DateTime? Date = null,
    IReadOnlyList<SalePayment>? Payments = null, bool OnCredit = false);

/// <summary>Часть оплаты продажи: сумма, поступившая в конкретную кассу (смешанная оплата).</summary>
public record SalePayment(int CashRegisterId, decimal Amount);

public record ReturnLine(int SaleItemId, decimal Quantity);

public record SaleReturnRequest(int SaleId, int CashRegisterId, IReadOnlyList<ReturnLine> Lines, string? Reason = null, DateTime? Date = null);

/// <summary>Фильтр журнала продаж. Search ищет по номеру, покупателю и комментарию.</summary>
public sealed record SaleFilter(DateTime From, DateTime To, int? WarehouseId = null, int? CashRegisterId = null,
    int? CustomerId = null, string? Search = null);

/// <summary>Страница журнала продаж и итоги по всему отфильтрованному периоду.</summary>
public sealed record SalesPage(IReadOnlyList<Sale> Items, int TotalCount, decimal Total, decimal Returned);

/// <summary>Продажи и возвраты. Каждая операция атомарна: документ + склад + касса.</summary>
public class SalesService(IDbContextFactory<TradeDbContext> factory, UserSession session)
{
    public async Task<Sale> CreateSaleAsync(SaleRequest request, CancellationToken ct = default)
    {
        Access.Demand(Perm.SalesCreate);
        var lines = ValidLines(request);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var sale = new Sale
        {
            Number = await DocumentNumbers.NextAsync(db, DocumentNumbers.Sale, ct),
            Date = request.Date ?? DateTime.Now,
            UserId = session.UserId
        };
        db.Sales.Add(sale);
        await PostAsync(db, sale, request, lines, ct);
        await tx.CommitAsync(ct);
        return sale;
    }

    /// <summary>
    /// Изменение проведённой продажи: старое списание и поступление в кассу отменяются, документ проводится заново
    /// с тем же номером и датой. Продажу с возвратами изменить нельзя.
    /// </summary>
    public async Task<Sale> UpdateSaleAsync(int id, SaleRequest request, CancellationToken ct = default)
    {
        Access.Demand(Perm.SalesEdit);
        var lines = ValidLines(request);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var sale = await LoadModifiableAsync(db, id, "изменить", ct);
        var touched = await UnpostAsync(db, sale, ct);
        db.SaleItems.RemoveRange(sale.Items);
        sale.Items.Clear();
        if (request.Date is { } d) sale.Date = d;
        await PostAsync(db, sale, request, lines, ct);
        EnsureNotNegative(touched);
        await tx.CommitAsync(ct);
        return sale;
    }

    /// <summary>Удаление продажи: товар возвращается на склад, сумма снимается с кассы.</summary>
    public async Task DeleteSaleAsync(int id, CancellationToken ct = default)
    {
        Access.Demand(Perm.SalesDelete);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var sale = await LoadModifiableAsync(db, id, "удалить", ct);
        EnsureNotNegative(await UnpostAsync(db, sale, ct));
        db.Sales.Remove(sale);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static List<DocumentLine> ValidLines(SaleRequest request)
    {
        var lines = request.Lines.Where(l => l.Quantity != 0).ToList();
        if (lines.Count == 0) throw new BusinessException("Добавьте хотя бы один товар.");
        if (lines.Any(l => l.Quantity < 0)) throw new BusinessException("Количество не может быть отрицательным.");
        if (lines.Any(l => l.Price < 0)) throw new BusinessException("Цена не может быть отрицательной.");
        return lines;
    }

    private static async Task<Sale> LoadModifiableAsync(TradeDbContext db, int id, string action, CancellationToken ct)
    {
        var sale = await db.Sales.Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == id, ct)
                   ?? throw new BusinessException("Продажа не найдена.");
        if (sale.ReturnedTotal > 0 || await db.SaleReturns.AnyAsync(r => r.SaleId == id, ct))
            throw new BusinessException("По продаже {0} оформлен возврат — " + action + " её нельзя.", sale.Number);
        return sale;
    }

    /// <summary>Списание товара, строки и поступление денег в кассу (документ уже добавлен в контекст).</summary>
    private async Task PostAsync(TradeDbContext db, Sale sale, SaleRequest request, List<DocumentLine> lines, CancellationToken ct)
    {
        var settings = await db.CompanySettings.FirstAsync(ct);
        sale.CustomerId = request.CustomerId;
        sale.WarehouseId = request.WarehouseId;
        sale.CashRegisterId = request.CashRegisterId;
        sale.Comment = request.Comment;
        await db.SaveChangesAsync(ct); // получаем Id документа

        foreach (var line in lines)
        {
            var qty = Money.RoundQty(line.Quantity);
            var unitCost = await StockLedger.IssueAsync(db, line.ProductId, sale.WarehouseId, qty,
                StockMovementType.Sale, sale.Id, sale.Number, sale.Date, settings.AllowNegativeStock, ct);
            sale.Items.Add(new SaleItem
            {
                ProductId = line.ProductId,
                Quantity = qty,
                Price = Money.Round(line.Price),
                Amount = Money.Round(qty * line.Price),
                UnitCost = unitCost
            });
        }
        sale.Total = sale.Items.Sum(i => i.Amount);
        sale.CostTotal = Money.Round(sale.Items.Sum(i => i.Quantity * i.UnitCost));

        var payments = SplitPayments(request, sale.Total);
        sale.PaidAmount = payments.Sum(p => p.Amount);
        foreach (var p in payments)
            await CashLedger.PostAsync(db, p.CashRegisterId, CashDirection.Income, CashOperationKind.Sale, "Продажа",
                p.Amount, null, sale.Date, sale.Id, sale.Number, session.UserId, ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Отмена проведения: товар обратно на склад (по себестоимости списания), деньги — из кассы.</summary>
    /// <returns>Кассы, из которых сняты деньги: их остаток проверяется после проведения (при изменении сумма вернётся).</returns>
    private static async Task<List<CashRegister>> UnpostAsync(TradeDbContext db, Sale sale, CancellationToken ct)
    {
        var touched = new List<CashRegister>();
        foreach (var item in sale.Items)
        {
            await StockLedger.RestoreIssueAsync(db, item.ProductId, sale.WarehouseId, item.Quantity, item.UnitCost,
                sale.Date, StockMovementType.Sale, sale.Id, ct);
        }
        db.StockMovements.RemoveRange(await db.StockMovements
            .Where(m => m.DocumentId == sale.Id && m.Type == StockMovementType.Sale).ToListAsync(ct));

        var payments = await db.CashTransactions.Where(t => t.DocumentId == sale.Id && t.Kind == CashOperationKind.Sale).ToListAsync(ct);
        foreach (var p in payments)
        {
            var cash = await db.CashRegisters.FindAsync([p.CashRegisterId], ct) ?? throw new BusinessException("Касса не найдена.");
            cash.Balance = Money.Round(cash.Balance - p.Amount);
            touched.Add(cash);
        }
        db.CashTransactions.RemoveRange(payments);
        await db.SaveChangesAsync(ct);
        return touched;
    }

    /// <summary>
    /// Разбивка суммы продажи по кассам. Без Payments — всё в основную кассу; иначе сумма частей должна совпасть с итогом.
    /// В долг (OnCredit) — оплаты могут быть меньше итога или отсутствовать: остаток записывается на покупателя.
    /// </summary>
    private static List<SalePayment> SplitPayments(SaleRequest request, decimal total)
    {
        if (total <= 0) return [];
        if (request.OnCredit && request.CustomerId is null)
            throw new BusinessException("Для продажи в долг выберите покупателя.");
        var parts = request.Payments ?? [];
        if (parts.Count == 0 && !request.OnCredit) return [new SalePayment(request.CashRegisterId, total)];
        if (parts.Any(p => p.Amount < 0)) throw new BusinessException("Сумма оплаты не может быть отрицательной.");
        var merged = parts.Where(p => p.Amount > 0).GroupBy(p => p.CashRegisterId)
            .Select(g => new SalePayment(g.Key, Money.Round(g.Sum(p => p.Amount)))).ToList();
        var sum = merged.Sum(p => p.Amount);
        if (request.OnCredit)
        {
            if (sum > total)
                throw new BusinessException("Сумма оплат ({0}) больше суммы продажи ({1}).", Money.Format(sum), Money.Format(total));
            return merged;
        }
        if (sum != total)
            throw new BusinessException("Сумма оплат ({0}) не совпадает с суммой продажи ({1}).", Money.Format(sum), Money.Format(total));
        return merged;
    }

    private static void EnsureNotNegative(IEnumerable<CashRegister> cash)
    {
        foreach (var c in cash)
            if (c.Balance < 0)
                throw new BusinessException("Недостаточно средств в кассе «{0}»: остаток станет {1}.", c.Name, Money.Format(c.Balance));
    }

    /// <summary>Страница журнала продаж (новые сверху) и итоги по фильтру.</summary>
    public async Task<SalesPage> GetSalesPageAsync(SaleFilter f, int skip, int take, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Sales.AsNoTracking().Where(s => s.Date >= f.From.Date && s.Date < f.To.Date.AddDays(1));
        if (f.WarehouseId is { } w) q = q.Where(s => s.WarehouseId == w);
        if (f.CashRegisterId is { } c) q = q.Where(s => s.CashRegisterId == c);
        if (f.CustomerId is { } cu) q = q.Where(s => s.CustomerId == cu);
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            // SQLite сравнивает кириллицу с учётом регистра — ищем несколько вариантов написания
            var term = f.Search.Trim();
            var lower = term.ToLowerInvariant();
            var upper = term.ToUpperInvariant();
            var title = char.ToUpperInvariant(lower[0]) + lower[1..];
            q = q.Where(s => s.Number.Contains(term) || s.Number.Contains(upper)
                || (s.Comment != null && (s.Comment.Contains(term) || s.Comment.Contains(lower) || s.Comment.Contains(title)))
                || (s.Customer != null && (s.Customer.Name.Contains(term) || s.Customer.Name.Contains(lower) || s.Customer.Name.Contains(title))));
        }

        var count = await q.CountAsync(ct);
        // SQLite не суммирует decimal на сервере — итоги считаем по выборке сумм
        var sums = await q.Select(s => new { s.Total, s.ReturnedTotal }).ToListAsync(ct);
        var items = await q.Include(s => s.Customer).Include(s => s.Warehouse).Include(s => s.CashRegister)
            .OrderByDescending(s => s.Date).ThenByDescending(s => s.Id).Skip(skip).Take(take).ToListAsync(ct);
        return new SalesPage(items, count, sums.Sum(s => s.Total), sums.Sum(s => s.ReturnedTotal));
    }

    public async Task<SaleReturn> CreateReturnAsync(SaleReturnRequest request, CancellationToken ct = default)
    {
        Access.Demand(Perm.SalesReturn);
        var lines = request.Lines.Where(l => l.Quantity > 0).ToList();
        if (lines.Count == 0) throw new BusinessException("Укажите количество возвращаемого товара.");

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var sale = await db.Sales.Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == request.SaleId, ct)
                   ?? throw new BusinessException("Продажа не найдена.");
        var date = request.Date ?? DateTime.Now;

        var ret = new SaleReturn
        {
            Number = await DocumentNumbers.NextAsync(db, DocumentNumbers.SaleReturn, ct),
            Date = date,
            SaleId = sale.Id,
            CashRegisterId = request.CashRegisterId,
            Reason = request.Reason,
            UserId = session.UserId
        };
        db.SaleReturns.Add(ret);
        await db.SaveChangesAsync(ct);

        foreach (var line in lines)
        {
            var item = sale.Items.FirstOrDefault(i => i.Id == line.SaleItemId)
                       ?? throw new BusinessException("Позиция продажи не найдена.");
            var qty = Money.RoundQty(line.Quantity);
            if (qty > item.Quantity - item.ReturnedQuantity)
                throw new BusinessException("Нельзя вернуть больше, чем продано: доступно к возврату {0:0.###}.", item.Quantity - item.ReturnedQuantity);

            item.ReturnedQuantity += qty;
            // Товар возвращается на склад по той же себестоимости, по которой был списан
            await StockLedger.ReceiveAsync(db, item.ProductId, sale.WarehouseId, qty, item.UnitCost,
                StockMovementType.SaleReturn, ret.Id, ret.Number, date, ct);
            ret.Items.Add(new SaleReturnItem
            {
                SaleItemId = item.Id,
                ProductId = item.ProductId,
                Quantity = qty,
                Price = item.Price,
                Amount = Money.Round(qty * item.Price),
                UnitCost = item.UnitCost
            });
        }
        ret.Total = ret.Items.Sum(i => i.Amount);
        ret.CostTotal = Money.Round(ret.Items.Sum(i => i.Quantity * i.UnitCost));
        sale.ReturnedTotal += ret.Total;

        // Продажа в долг: возврат сначала уменьшает неоплаченный долг покупателя, деньги выдаются только сверх него
        var refund = Money.Round(ret.Total - Math.Min(ret.Total, await DebtOffsetLimitAsync(db, sale, ret.Id, ct)));
        if (refund > 0)
        {
            var op = await CashLedger.PostAsync(db, ret.CashRegisterId, CashDirection.Expense, CashOperationKind.SaleReturn,
                "Возврат покупателю", refund, $"Возврат по {sale.Number}", date, ret.Id, ret.Number, session.UserId, ct);
            op.CounterpartyId = sale.CustomerId;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ret;
    }

    /// <summary>
    /// Сколько из суммы возврата по продаже можно зачесть в долг покупателя, не выдавая денег:
    /// неоплаченная часть продажи (за вычетом уже зачтённых возвратов), но не больше текущего долга покупателя
    /// (если он уже расплатился платежом — деньги возвращаются из кассы).
    /// </summary>
    public async Task<decimal> GetReturnDebtOffsetLimitAsync(int saleId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var sale = await db.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.Id == saleId, ct)
                   ?? throw new BusinessException("Продажа не найдена.");
        return await DebtOffsetLimitAsync(db, sale, null, ct);
    }

    private static async Task<decimal> DebtOffsetLimitAsync(TradeDbContext db, Sale sale, int? currentReturnId, CancellationToken ct)
    {
        if (sale.CustomerId is not { } customerId || sale.Total <= sale.PaidAmount) return 0;
        var prior = await db.SaleReturns.AsNoTracking().Where(r => r.SaleId == sale.Id && r.Id != currentReturnId)
            .Select(r => new { r.Id, r.Total }).ToListAsync(ct);
        var priorIds = prior.Select(r => (int?)r.Id).ToList();
        var priorRefunds = (await db.CashTransactions.AsNoTracking()
            .Where(t => t.Kind == CashOperationKind.SaleReturn && priorIds.Contains(t.DocumentId))
            .Select(t => t.Amount).ToListAsync(ct)).Sum();
        var saleDebt = sale.Total - sale.PaidAmount - (prior.Sum(r => r.Total) - priorRefunds);
        var customerDebt = (await CounterpartyLedger.BalancesAsync(db, [customerId], null, ct)).GetValueOrDefault(customerId);
        return Money.Round(Math.Max(0, Math.Min(saleDebt, customerDebt)));
    }

    public async Task<List<Sale>> GetSalesAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Sales.AsNoTracking()
            .Include(s => s.Customer).Include(s => s.Warehouse).Include(s => s.CashRegister)
            .Where(s => s.Date >= from.Date && s.Date < to.Date.AddDays(1))
            .OrderByDescending(s => s.Date).ToListAsync(ct);
    }

    public async Task<Sale?> GetSaleAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Sales.AsNoTracking()
            .Include(s => s.Customer).Include(s => s.Warehouse).Include(s => s.CashRegister)
            .Include(s => s.Items).ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<List<SaleReturn>> GetReturnsAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.SaleReturns.AsNoTracking()
            .Include(r => r.Sale).Include(r => r.CashRegister)
            .Where(r => r.Date >= from.Date && r.Date < to.Date.AddDays(1))
            .OrderByDescending(r => r.Date).ToListAsync(ct);
    }
}
