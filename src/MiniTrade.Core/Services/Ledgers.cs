using MiniTrade.Core.Common;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Services;

/// <summary>
/// Складской регистр: остаток + средняя себестоимость (метод средневзвешенной).
/// Все изменения выполняются в рамках транзакции вызывающего сервиса.
/// </summary>
internal static class StockLedger
{
    public static async Task<StockBalance> GetOrCreateAsync(TradeDbContext db, int productId, int warehouseId, CancellationToken ct)
    {
        var balance = db.StockBalances.Local.FirstOrDefault(x => x.ProductId == productId && x.WarehouseId == warehouseId)
                      ?? await db.StockBalances.FirstOrDefaultAsync(x => x.ProductId == productId && x.WarehouseId == warehouseId, ct);
        if (balance is null)
        {
            balance = new StockBalance { ProductId = productId, WarehouseId = warehouseId };
            db.StockBalances.Add(balance);
        }
        return balance;
    }

    /// <summary>Метод себестоимости из настроек (FIFO, если настройки ещё не созданы).</summary>
    public static async Task<CostingMethod> MethodAsync(TradeDbContext db, CancellationToken ct) =>
        db.CompanySettings.Local.FirstOrDefault()?.CostingMethod
        ?? await db.CompanySettings.AsNoTracking().Select(s => s.CostingMethod).FirstOrDefaultAsync(ct);

    /// <summary>Приход товара: новая партия и пересчёт средней себестоимости.</summary>
    public static async Task ReceiveAsync(TradeDbContext db, int productId, int warehouseId, decimal qty, decimal unitCost,
        StockMovementType type, int documentId, string documentNumber, DateTime date, CancellationToken ct)
    {
        if (qty <= 0) throw new BusinessException("Количество прихода должно быть больше нуля.");
        var balance = await GetOrCreateAsync(db, productId, warehouseId, ct);
        AddToBalance(balance, qty, unitCost);
        await AddLotAsync(db, balance, qty, unitCost, date, type, documentId, ct);

        db.StockMovements.Add(new StockMovement
        {
            Date = date, ProductId = productId, WarehouseId = warehouseId, Quantity = qty,
            UnitCost = Money.RoundCost(unitCost), Type = type, DocumentId = documentId, DocumentNumber = documentNumber
        });
    }

    /// <summary>
    /// Расход товара. Возвращает себестоимость единицы: по FIFO — стоимость списанных партий,
    /// по средней — средняя остатка. Партии списываются в любом режиме (от ранних к поздним).
    /// </summary>
    public static async Task<decimal> IssueAsync(TradeDbContext db, int productId, int warehouseId, decimal qty,
        StockMovementType type, int documentId, string documentNumber, DateTime date, bool allowNegative, CancellationToken ct)
    {
        if (qty <= 0) throw new BusinessException("Количество расхода должно быть больше нуля.");
        var balance = await GetOrCreateAsync(db, productId, warehouseId, ct);
        if (!allowNegative && balance.Quantity < qty)
        {
            var product = await db.Products.FindAsync([productId], ct);
            throw new BusinessException("Недостаточно товара «{0}» на складе: доступно {1:0.###}, требуется {2:0.###}.",
                product?.Name, balance.Quantity, qty);
        }
        var fallback = balance.AvgCost;
        if (fallback == 0)
            fallback = (await db.Products.FindAsync([productId], ct))?.PurchasePrice ?? 0;

        var lots = await LotsAsync(db, productId, warehouseId, ct);
        var (taken, value) = Consume(lots, qty, db, type, documentId);
        var method = await MethodAsync(db, ct);
        // Не хватило партий (продажа в минус) — недостающее по средней / закупочной цене
        var unitCost = method == CostingMethod.Fifo
            ? Money.RoundCost((value + (qty - taken) * fallback) / qty)
            : fallback;

        balance.Quantity = Money.RoundQty(balance.Quantity - qty);
        if (method == CostingMethod.Fifo) SyncAverage(balance, lots);

        db.StockMovements.Add(new StockMovement
        {
            Date = date, ProductId = productId, WarehouseId = warehouseId, Quantity = -qty,
            UnitCost = unitCost, Type = type, DocumentId = documentId, DocumentNumber = documentNumber
        });
        return unitCost;
    }

    /// <summary>
    /// Сторно прихода (изменение/удаление закупки, возврат поставщику): товар снимается по цене прихода,
    /// в первую очередь из партии этого документа. Движение по складу пишет вызывающий код.
    /// </summary>
    public static async Task RemoveReceiptAsync(TradeDbContext db, int productId, int warehouseId, decimal qty, decimal unitCost,
        StockMovementType sourceType, int sourceDocumentId, CancellationToken ct)
    {
        var balance = await GetOrCreateAsync(db, productId, warehouseId, ct);
        var newQty = Money.RoundQty(balance.Quantity - qty);
        if (newQty > 0 && balance.Quantity > 0)
            balance.AvgCost = Money.RoundCost(Math.Max(0, (balance.Quantity * balance.AvgCost - qty * unitCost) / newQty));
        balance.Quantity = newQty;

        var lots = await LotsAsync(db, productId, warehouseId, ct);
        var own = lots.Where(l => l.DocumentType == sourceType && l.DocumentId == sourceDocumentId).ToList();
        var (taken, _) = Consume(own, qty);
        if (taken < qty) Consume(lots, qty - taken);
        if (await MethodAsync(db, ct) == CostingMethod.Fifo) SyncAverage(balance, lots);
    }

    /// <summary>
    /// Сторно расхода (изменение/удаление продажи): товар возвращается в те партии, из которых был списан.
    /// То, что продавалось в минус (без партий), приходуется новой партией по себестоимости списания.
    /// </summary>
    public static async Task RestoreIssueAsync(TradeDbContext db, int productId, int warehouseId, decimal qty, decimal unitCost,
        DateTime date, StockMovementType type, int documentId, CancellationToken ct)
    {
        if (qty <= 0) return;
        var balance = await GetOrCreateAsync(db, productId, warehouseId, ct);
        var usages = await db.StockLotUsages.Include(u => u.Lot)
            .Where(u => u.DocumentType == type && u.DocumentId == documentId && u.Lot!.ProductId == productId && u.Lot.WarehouseId == warehouseId)
            .OrderByDescending(u => u.Id).ToListAsync(ct);
        var left = qty;
        foreach (var u in usages)
        {
            if (left <= 0) break;
            var part = Math.Min(left, u.Quantity);
            u.Lot!.Quantity = Money.RoundQty(u.Lot.Quantity + part);
            u.Quantity = Money.RoundQty(u.Quantity - part);
            if (u.Quantity <= 0) db.StockLotUsages.Remove(u);
            left -= part;
        }
        AddToBalance(balance, qty, unitCost);
        if (left > 0) await AddLotAsync(db, balance, left, unitCost, date, type, documentId, ct);
        else if (await MethodAsync(db, ct) == CostingMethod.Fifo) SyncAverage(balance, await LotsAsync(db, productId, warehouseId, ct));
    }

    private static void AddToBalance(StockBalance balance, decimal qty, decimal unitCost)
    {
        var newQty = balance.Quantity + qty;
        if (balance.Quantity <= 0)
            balance.AvgCost = Money.RoundCost(unitCost);
        else if (newQty > 0)
            balance.AvgCost = Money.RoundCost((balance.Quantity * balance.AvgCost + qty * unitCost) / newQty);
        balance.Quantity = Money.RoundQty(newQty);
    }

    private static async Task AddLotAsync(TradeDbContext db, StockBalance balance, decimal qty, decimal unitCost, DateTime date,
        StockMovementType type, int documentId, CancellationToken ct)
    {
        var lots = await LotsAsync(db, balance.ProductId, balance.WarehouseId, ct);
        var lot = new StockLot
        {
            ProductId = balance.ProductId, WarehouseId = balance.WarehouseId, Date = date, UnitCost = Money.RoundCost(unitCost),
            InitialQuantity = qty, Quantity = qty, DocumentType = type, DocumentId = documentId
        };
        db.StockLots.Add(lot);
        if (await MethodAsync(db, ct) == CostingMethod.Fifo) SyncAverage(balance, [.. lots, lot]);
    }

    /// <summary>Непустые партии товара на складе (из базы и ещё не сохранённые), от ранних к поздним.</summary>
    private static async Task<List<StockLot>> LotsAsync(TradeDbContext db, int productId, int warehouseId, CancellationToken ct)
    {
        await db.StockLots.Where(l => l.ProductId == productId && l.WarehouseId == warehouseId && l.Quantity > 0).LoadAsync(ct);
        return db.StockLots.Local
            .Where(l => l.ProductId == productId && l.WarehouseId == warehouseId && l.Quantity > 0)
            .OrderBy(l => l.Date).ThenBy(l => l.Id <= 0 ? int.MaxValue : l.Id).ToList();
    }

    /// <summary>
    /// Списывает до qty из партий по порядку. Возвращает списанное количество и его стоимость.
    /// С указанным документом расхода запоминает, из каких партий списано, — для точного сторно.
    /// </summary>
    private static (decimal Taken, decimal Value) Consume(IEnumerable<StockLot> lots, decimal qty,
        TradeDbContext? db = null, StockMovementType type = default, int documentId = 0)
    {
        decimal left = qty, value = 0;
        foreach (var lot in lots)
        {
            if (left <= 0) break;
            if (lot.Quantity <= 0) continue;
            var part = Math.Min(left, lot.Quantity);
            lot.Quantity = Money.RoundQty(lot.Quantity - part);
            value += part * lot.UnitCost;
            left -= part;
            db?.StockLotUsages.Add(new StockLotUsage { Lot = lot, Quantity = part, DocumentType = type, DocumentId = documentId });
        }
        return (qty - left, value);
    }

    /// <summary>По FIFO средняя себестоимость остатка — средняя по оставшимся партиям (для оценки склада).</summary>
    private static void SyncAverage(StockBalance balance, IEnumerable<StockLot> lots)
    {
        var rest = lots.Where(l => l.Quantity > 0).ToList();
        var q = rest.Sum(l => l.Quantity);
        if (q > 0) balance.AvgCost = Money.RoundCost(rest.Sum(l => l.Quantity * l.UnitCost) / q);
    }
}

/// <summary>Кассовый регистр: журнал операций + остаток кассы.</summary>
internal static class CashLedger
{
    public static async Task<CashTransaction> PostAsync(TradeDbContext db, int cashRegisterId, CashDirection direction,
        CashOperationKind kind, string category, decimal amount, string? comment, DateTime date,
        int? documentId, string? documentNumber, int? userId, CancellationToken ct)
    {
        amount = Money.Round(amount);
        if (amount <= 0) throw new BusinessException("Сумма операции должна быть больше нуля.");
        var cash = await db.CashRegisters.FindAsync([cashRegisterId], ct)
                   ?? throw new BusinessException("Касса не найдена.");
        if (direction == CashDirection.Expense && cash.Balance < amount)
            throw new BusinessException("Недостаточно средств в кассе «{0}»: остаток {1}, требуется {2}.",
                cash.Name, Money.Format(cash.Balance), Money.Format(amount));

        cash.Balance = Money.Round(cash.Balance + (direction == CashDirection.Income ? amount : -amount));
        var tx = new CashTransaction
        {
            Date = date, CashRegisterId = cashRegisterId, Direction = direction, Kind = kind, Category = category,
            Amount = amount, Comment = comment, DocumentId = documentId, DocumentNumber = documentNumber, UserId = userId
        };
        db.CashTransactions.Add(tx);
        return tx;
    }
}

/// <summary>Сквозная нумерация документов по префиксу.</summary>
internal static class DocumentNumbers
{
    public const string Sale = "ПР";
    public const string SaleReturn = "ВЗ";
    public const string Purchase = "ЗК";
    public const string PurchaseReturn = "ВП";
    public const string Transfer = "ПМ";
    public const string Inventory = "ИН";
    public const string Cash = "КО";

    public static async Task<string> NextAsync(TradeDbContext db, string prefix, CancellationToken ct)
    {
        var seq = await db.DocumentSequences.FindAsync([prefix], ct);
        if (seq is null)
        {
            seq = new DocumentSequence { Key = prefix, Value = 0 };
            db.DocumentSequences.Add(seq);
        }
        seq.Value++;
        return $"{prefix}-{seq.Value:D6}";
    }
}
