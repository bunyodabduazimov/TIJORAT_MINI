using MiniTrade.Core.Common;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Services;

/// <summary>
/// Приход от поставщика. PaidAmount — оплата, проводимая вместе с документом (при изменении — доплата сверх уже оплаченного).
/// </summary>
public record PurchaseRequest(int SupplierId, int WarehouseId, IReadOnlyList<DocumentLine> Lines,
    decimal PaidAmount = 0, int? CashRegisterId = null, string? Comment = null, DateTime? Date = null,
    bool UpdateProductPurchasePrice = true);

public record PurchaseReturnLine(int PurchaseItemId, decimal Quantity);

/// <summary>Возврат поставщику. CashRegisterId нужен, если закупка оплачена больше суммы после возврата.</summary>
public record PurchaseReturnRequest(int PurchaseId, IReadOnlyList<PurchaseReturnLine> Lines, int? CashRegisterId = null,
    string? Reason = null, DateTime? Date = null);

/// <summary>Фильтр журнала закупок. Search ищет по номеру, поставщику и комментарию.</summary>
public sealed record PurchaseFilter(DateTime From, DateTime To, int? SupplierId = null, int? WarehouseId = null,
    bool OnlyWithDebt = false, string? Search = null);

/// <summary>Страница журнала закупок и итоги по всему отфильтрованному периоду.</summary>
public sealed record PurchasesPage(IReadOnlyList<Purchase> Items, int TotalCount, decimal Total, decimal Paid)
{
    public decimal Debt => Total - Paid;
}

/// <summary>Расчёты с поставщиком за всё время.</summary>
public sealed record SupplierDebtRow(int SupplierId, string Supplier, string? Phone, int Documents, decimal Total, decimal Paid, DateTime LastDate)
{
    public decimal Debt => Total - Paid;
}

/// <summary>Закупки у поставщиков: приход на склад, оплата из кассы, изменение и удаление документов.</summary>
public class PurchaseService(IDbContextFactory<TradeDbContext> factory, UserSession session)
{
    private const string PaymentArticle = "Оплата поставщику";

    public async Task<Purchase> CreatePurchaseAsync(PurchaseRequest request, CancellationToken ct = default)
    {
        var lines = ValidLines(request);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var purchase = new Purchase
        {
            Number = await DocumentNumbers.NextAsync(db, DocumentNumbers.Purchase, ct),
            Date = request.Date ?? DateTime.Now,
            UserId = session.UserId
        };
        db.Purchases.Add(purchase);
        await PostAsync(db, purchase, request, lines, ct);
        await tx.CommitAsync(ct);
        return purchase;
    }

    /// <summary>
    /// Изменение проведённой закупки: приход сторнируется и проводится заново с тем же номером.
    /// Сделанные оплаты сохраняются; новая сумма не может быть меньше уже оплаченного.
    /// </summary>
    public async Task<Purchase> UpdatePurchaseAsync(int id, PurchaseRequest request, CancellationToken ct = default)
    {
        var lines = ValidLines(request);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var purchase = await LoadModifiableAsync(db, id, "изменить", ct);

        var touched = await UnpostStockAsync(db, purchase, ct);
        db.PurchaseItems.RemoveRange(purchase.Items);
        purchase.Items.Clear();
        if (request.Date is { } d) purchase.Date = d;
        await PostAsync(db, purchase, request, lines, ct);
        await EnsureStockNotNegativeAsync(db, touched, ct);
        // Сменился поставщик — прежние оплаты по документу относятся к новому
        foreach (var p in await LinkedPaymentsQuery(db, purchase).ToListAsync(ct)) p.CounterpartyId = purchase.SupplierId;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return purchase;
    }

    /// <summary>Удаление закупки: товар списывается со склада, оплаты по документу возвращаются в кассы.</summary>
    public async Task DeletePurchaseAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var purchase = await LoadModifiableAsync(db, id, "удалить", ct);

        var touched = await UnpostStockAsync(db, purchase, ct);
        await EnsureStockNotNegativeAsync(db, touched, ct);

        var payments = await LinkedPaymentsQuery(db, purchase).ToListAsync(ct);
        foreach (var p in payments)
        {
            var cash = await db.CashRegisters.FindAsync([p.CashRegisterId], ct) ?? throw new BusinessException("Касса не найдена.");
            cash.Balance = Money.Round(cash.Balance + p.Amount);
        }
        db.CashTransactions.RemoveRange(payments);
        db.Purchases.Remove(purchase);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Погашение задолженности по конкретной закупке.</summary>
    public async Task PayAsync(int purchaseId, decimal amount, int cashRegisterId, string? comment = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var purchase = await db.Purchases.FindAsync([purchaseId], ct) ?? throw new BusinessException("Закупка не найдена.");
        amount = Money.Round(amount);
        if (amount <= 0) throw new BusinessException("Сумма оплаты должна быть больше нуля.");
        if (amount > purchase.Debt) throw new BusinessException("Сумма превышает задолженность ({0}).", Money.Format(purchase.Debt));

        var op = await CashLedger.PostAsync(db, cashRegisterId, CashDirection.Expense, CashOperationKind.SupplierPayment,
            PaymentArticle, amount, comment, DateTime.Now, purchase.Id, purchase.Number, session.UserId, ct);
        op.CounterpartyId = purchase.SupplierId;
        purchase.PaidAmount = Money.Round(purchase.PaidAmount + amount);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Возврат товара поставщику. Сумма закупки уменьшается: сначала гасится долг,
    /// а если закупка уже оплачена больше новой суммы — поставщик возвращает разницу в кассу.
    /// </summary>
    public async Task<PurchaseReturn> CreateReturnAsync(PurchaseReturnRequest request, CancellationToken ct = default)
    {
        var lines = request.Lines.Where(l => l.Quantity > 0).ToList();
        if (lines.Count == 0) throw new BusinessException("Укажите количество возвращаемого товара.");

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var purchase = await db.Purchases.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == request.PurchaseId, ct)
                       ?? throw new BusinessException("Закупка не найдена.");
        var allowNegative = await db.CompanySettings.Select(s => s.AllowNegativeStock).FirstAsync(ct);
        var date = request.Date ?? DateTime.Now;

        var ret = new PurchaseReturn
        {
            Number = await DocumentNumbers.NextAsync(db, DocumentNumbers.PurchaseReturn, ct),
            Date = date,
            PurchaseId = purchase.Id,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            UserId = session.UserId
        };
        db.PurchaseReturns.Add(ret);
        await db.SaveChangesAsync(ct);

        foreach (var line in lines)
        {
            var item = purchase.Items.FirstOrDefault(i => i.Id == line.PurchaseItemId)
                       ?? throw new BusinessException("Позиция закупки не найдена.");
            var qty = Money.RoundQty(line.Quantity);
            if (qty > item.Quantity - item.ReturnedQuantity)
                throw new BusinessException("Нельзя вернуть больше, чем закуплено: доступно к возврату {0:0.###}.", item.Quantity - item.ReturnedQuantity);

            // Товар уходит со склада по цене закупки — средняя себестоимость пересчитывается обратно
            var balance = await StockLedger.GetOrCreateAsync(db, item.ProductId, purchase.WarehouseId, ct);
            if (!allowNegative && balance.Quantity < qty)
            {
                var product = await db.Products.FindAsync([item.ProductId], ct);
                throw new BusinessException("Недостаточно товара «{0}» на складе: доступно {1:0.###}, к возврату {2:0.###}.",
                    product?.Name, balance.Quantity, qty);
            }
            await StockLedger.RemoveReceiptAsync(db, item.ProductId, purchase.WarehouseId, qty, item.Price,
                StockMovementType.Purchase, purchase.Id, ct);
            db.StockMovements.Add(new StockMovement
            {
                Date = date, ProductId = item.ProductId, WarehouseId = purchase.WarehouseId, Quantity = -qty,
                UnitCost = item.Price, Type = StockMovementType.PurchaseReturn, DocumentId = ret.Id, DocumentNumber = ret.Number
            });

            item.ReturnedQuantity = Money.RoundQty(item.ReturnedQuantity + qty);
            ret.Items.Add(new PurchaseReturnItem
            {
                PurchaseItemId = item.Id, ProductId = item.ProductId, Quantity = qty,
                Price = item.Price, Amount = Money.Round(qty * item.Price)
            });
        }
        ret.Total = ret.Items.Sum(i => i.Amount);
        purchase.Total = Money.Round(purchase.Total - ret.Total);
        purchase.ReturnedTotal = Money.Round(purchase.ReturnedTotal + ret.Total);

        // Оплачено больше новой суммы закупки — поставщик возвращает разницу
        var refund = Money.Round(purchase.PaidAmount - purchase.Total);
        if (refund > 0)
        {
            if (request.CashRegisterId is not { } cashId) throw new BusinessException("Выберите кассу, в которую поставщик вернёт деньги.");
            var op = await CashLedger.PostAsync(db, cashId, CashDirection.Income, CashOperationKind.PurchaseReturn,
                "Возврат от поставщика", refund, $"Возврат по {purchase.Number}", date, ret.Id, ret.Number, session.UserId, ct);
            op.CounterpartyId = purchase.SupplierId;
            purchase.PaidAmount = purchase.Total;
            ret.CashRegisterId = cashId;
            ret.RefundAmount = refund;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ret;
    }

    // ── Проведение ──

    private static List<DocumentLine> ValidLines(PurchaseRequest request)
    {
        var lines = request.Lines.Where(l => l.Quantity != 0).ToList();
        if (lines.Count == 0) throw new BusinessException("Добавьте хотя бы один товар.");
        if (lines.Any(l => l.Quantity < 0)) throw new BusinessException("Количество не может быть отрицательным.");
        if (lines.Any(l => l.Price < 0)) throw new BusinessException("Цена не может быть отрицательной.");
        if (request.PaidAmount < 0) throw new BusinessException("Сумма оплаты не может быть отрицательной.");
        if (request.PaidAmount > 0 && request.CashRegisterId is null) throw new BusinessException("Выберите кассу для оплаты.");
        return lines;
    }

    /// <summary>Реквизиты, приход товара, строки и оплата (документ уже добавлен в контекст).</summary>
    private async Task PostAsync(TradeDbContext db, Purchase purchase, PurchaseRequest request, List<DocumentLine> lines, CancellationToken ct)
    {
        if (!await db.Counterparties.AnyAsync(c => c.Id == request.SupplierId, ct)) throw new BusinessException("Выберите поставщика.");
        if (!await db.Warehouses.AnyAsync(w => w.Id == request.WarehouseId, ct)) throw new BusinessException("Выберите склад.");

        // Все товары документа — одним запросом
        var ids = lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        if (products.Count != ids.Count) throw new BusinessException("Товар не найден в справочнике.");

        purchase.SupplierId = request.SupplierId;
        purchase.WarehouseId = request.WarehouseId;
        purchase.Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        await db.SaveChangesAsync(ct); // получаем Id документа

        foreach (var line in lines)
        {
            var qty = Money.RoundQty(line.Quantity);
            var price = Money.Round(line.Price);
            await StockLedger.ReceiveAsync(db, line.ProductId, purchase.WarehouseId, qty, price,
                StockMovementType.Purchase, purchase.Id, purchase.Number, purchase.Date, ct);
            purchase.Items.Add(new PurchaseItem { ProductId = line.ProductId, Quantity = qty, Price = price, Amount = Money.Round(qty * price) });
            if (request.UpdateProductPurchasePrice) products[line.ProductId].PurchasePrice = price;
        }
        purchase.Total = purchase.Items.Sum(i => i.Amount);

        if (purchase.PaidAmount > purchase.Total)
            throw new BusinessException("Сумма закупки ({0}) меньше уже оплаченного ({1}).", Money.Format(purchase.Total), Money.Format(purchase.PaidAmount));
        var paid = Money.Round(request.PaidAmount);
        if (paid > purchase.Debt)
            throw new BusinessException("Оплата ({0}) больше суммы к оплате ({1}).", Money.Format(paid), Money.Format(purchase.Debt));
        if (paid > 0)
        {
            var op = await CashLedger.PostAsync(db, request.CashRegisterId!.Value, CashDirection.Expense, CashOperationKind.SupplierPayment,
                PaymentArticle, paid, null, DateTime.Now, purchase.Id, purchase.Number, session.UserId, ct);
            op.CounterpartyId = purchase.SupplierId;
            purchase.PaidAmount = Money.Round(purchase.PaidAmount + paid);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Изменять и удалять можно закупку, вся оплата которой проведена по самому документу.
    /// Платёж поставщику из «Финансов» распределяется по закупкам без привязки — такой документ защищён.
    /// </summary>
    private static async Task<Purchase> LoadModifiableAsync(TradeDbContext db, int id, string action, CancellationToken ct)
    {
        var purchase = await db.Purchases.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id, ct)
                       ?? throw new BusinessException("Закупка не найдена.");
        if (purchase.ReturnedTotal > 0)
            throw new BusinessException("По закупке {0} оформлен возврат поставщику — " + action + " её нельзя.", purchase.Number);
        var linked =(await LinkedPaymentsQuery(db, purchase).Select(t => t.Amount).ToListAsync(ct)).Sum();
        if (purchase.PaidAmount != linked)
            throw new BusinessException("Закупка {0} оплачена платежом поставщику из «Финансов» — " + action + " её нельзя. Сначала удалите этот платёж.",
                purchase.Number);
        return purchase;
    }

    private static IQueryable<CashTransaction> LinkedPaymentsQuery(TradeDbContext db, Purchase p) =>
        db.CashTransactions.Where(t => t.Kind == CashOperationKind.SupplierPayment && t.DocumentId == p.Id && t.DocumentNumber == p.Number);

    /// <summary>Сторно прихода: товар снимается со склада, средняя себестоимость пересчитывается обратно.</summary>
    private static async Task<List<StockBalance>> UnpostStockAsync(TradeDbContext db, Purchase purchase, CancellationToken ct)
    {
        var touched = new List<StockBalance>();
        foreach (var item in purchase.Items)
        {
            await StockLedger.RemoveReceiptAsync(db, item.ProductId, purchase.WarehouseId, item.Quantity, item.Price,
                StockMovementType.Purchase, purchase.Id, ct);
            var balance = await StockLedger.GetOrCreateAsync(db, item.ProductId, purchase.WarehouseId, ct);
            if (!touched.Contains(balance)) touched.Add(balance);
        }
        db.StockMovements.RemoveRange(await db.StockMovements
            .Where(m => m.DocumentId == purchase.Id && m.Type == StockMovementType.Purchase).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return touched;
    }

    /// <summary>Остаток после сторно не должен уйти в минус: значит, товар уже продан или перемещён.</summary>
    private static async Task EnsureStockNotNegativeAsync(TradeDbContext db, List<StockBalance> balances, CancellationToken ct)
    {
        if (await db.CompanySettings.Select(s => s.AllowNegativeStock).FirstAsync(ct)) return;
        foreach (var b in balances.Where(b => b.Quantity < 0))
        {
            var product = await db.Products.FindAsync([b.ProductId], ct);
            throw new BusinessException("Товар «{0}» из закупки уже продан или перемещён: остаток станет {1:0.###}.", product?.Name, b.Quantity);
        }
    }

    // ── Чтение ──

    /// <summary>Страница журнала закупок (новые сверху) и итоги по фильтру.</summary>
    public async Task<PurchasesPage> GetPurchasesPageAsync(PurchaseFilter f, int skip, int take, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Purchases.AsNoTracking().Where(p => p.Date >= f.From.Date && p.Date < f.To.Date.AddDays(1));
        if (f.SupplierId is { } s) q = q.Where(p => p.SupplierId == s);
        if (f.WarehouseId is { } w) q = q.Where(p => p.WarehouseId == w);
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            // SQLite сравнивает кириллицу с учётом регистра — ищем несколько вариантов написания
            var term = f.Search.Trim();
            var lower = term.ToLowerInvariant();
            var upper = term.ToUpperInvariant();
            var title = char.ToUpperInvariant(lower[0]) + lower[1..];
            q = q.Where(p => p.Number.Contains(term) || p.Number.Contains(upper)
                || (p.Comment != null && (p.Comment.Contains(term) || p.Comment.Contains(lower) || p.Comment.Contains(title)))
                || (p.Supplier != null && (p.Supplier.Name.Contains(term) || p.Supplier.Name.Contains(lower) || p.Supplier.Name.Contains(title))));
        }

        // SQLite хранит decimal как TEXT: суммы и признак долга считаем по лёгкой выборке, затем грузим нужную страницу
        var rows = (await q.Select(p => new { p.Id, p.Date, p.Total, p.PaidAmount }).ToListAsync(ct))
            .Where(r => !f.OnlyWithDebt || r.Total > r.PaidAmount)
            .OrderByDescending(r => r.Date).ThenByDescending(r => r.Id).ToList();
        var pageIds = rows.Skip(skip).Take(take).Select(r => r.Id).ToList();
        var items = await db.Purchases.AsNoTracking().Include(p => p.Supplier).Include(p => p.Warehouse)
            .Where(p => pageIds.Contains(p.Id)).ToListAsync(ct);
        var ordered = items.OrderBy(p => pageIds.IndexOf(p.Id)).ToList();
        return new PurchasesPage(ordered, rows.Count, rows.Sum(r => r.Total), rows.Sum(r => r.PaidAmount));
    }

    public async Task<List<Purchase>> GetPurchasesAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Purchases.AsNoTracking()
            .Include(p => p.Supplier).Include(p => p.Warehouse)
            .Where(p => p.Date >= from.Date && p.Date < to.Date.AddDays(1))
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).ToListAsync(ct);
    }

    public async Task<Purchase?> GetPurchaseAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Purchases.AsNoTracking()
            .Include(p => p.Supplier).Include(p => p.Warehouse)
            .Include(p => p.Items).ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    /// <summary>Оплаты, проведённые по документу закупки.</summary>
    public async Task<List<CashTransaction>> GetPaymentsAsync(int purchaseId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var purchase = await db.Purchases.AsNoTracking().FirstOrDefaultAsync(p => p.Id == purchaseId, ct);
        if (purchase is null) return [];
        return await LinkedPaymentsQuery(db, purchase).AsNoTracking().Include(t => t.CashRegister)
            .OrderBy(t => t.Date).ThenBy(t => t.Id).ToListAsync(ct);
    }

    /// <summary>Расчёты по каждому поставщику за всё время (долг — сначала самые крупные).</summary>
    public async Task<List<SupplierDebtRow>> GetSupplierDebtsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.Purchases.AsNoTracking()
            .Select(p => new { p.SupplierId, p.Supplier!.Name, p.Supplier.Phone, p.Date, p.Total, p.PaidAmount })
            .ToListAsync(ct);
        return list.GroupBy(p => p.SupplierId)
            .Select(g => new SupplierDebtRow(g.Key, g.First().Name, g.First().Phone, g.Count(),
                g.Sum(x => x.Total), g.Sum(x => x.PaidAmount), g.Max(x => x.Date)))
            .OrderByDescending(r => r.Debt).ThenBy(r => r.Supplier).ToList();
    }
}
