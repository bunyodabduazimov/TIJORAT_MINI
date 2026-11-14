using MiniTrade.Core.Common;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Services;

public record StockRow(int ProductId, string Product, string? Sku, string Unit, string? Category, int WarehouseId, string Warehouse,
    decimal Quantity, decimal AvgCost, decimal SalePrice, decimal MinStock)
{
    public decimal CostValue => Money.Round(Quantity * AvgCost);
    public decimal SaleValue => Money.Round(Quantity * SalePrice);
    public bool IsLow => MinStock > 0 && Quantity <= MinStock;
    public bool IsNegative => Quantity < 0;
}

public record TransferLine(int ProductId, decimal Quantity);

public record InventoryLine(int ProductId, decimal ActualQuantity);

/// <summary>Склад: остатки, перемещения между складами, инвентаризация.</summary>
public class WarehouseService(IDbContextFactory<TradeDbContext> factory)
{
    public async Task<List<StockRow>> GetBalancesAsync(int? warehouseId = null, bool includeZero = false, CancellationToken ct = default,
        int? productId = null, int? categoryId = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.StockBalances.AsNoTracking();
        if (warehouseId is not null) query = query.Where(b => b.WarehouseId == warehouseId);
        if (productId is not null) query = query.Where(b => b.ProductId == productId);
        if (categoryId is not null) query = query.Where(b => b.Product!.CategoryId == categoryId);
        // Проекция в SQL: не загружаем сущности товаров и складов целиком
        var rows = await query.Select(b => new StockRow(b.ProductId, b.Product!.Name, b.Product.Sku, b.Product.Unit,
                b.Product.Category != null ? b.Product.Category.Name : null,
                b.WarehouseId, b.Warehouse!.Name, b.Quantity, b.AvgCost, b.Product.SalePrice, b.Product.MinStock))
            .ToListAsync(ct);
        return rows
            .Where(r => includeZero || r.Quantity != 0)
            .OrderBy(r => r.Product, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Warehouse)
            .ToList();
    }

    /// <summary>Остатки склада: ProductId → (количество, ср. себестоимость).</summary>
    public async Task<Dictionary<int, (decimal Quantity, decimal AvgCost)>> GetWarehouseStockAsync(int warehouseId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.StockBalances.AsNoTracking().Where(b => b.WarehouseId == warehouseId)
            .Select(b => new { b.ProductId, b.Quantity, b.AvgCost }).ToListAsync(ct);
        return list.ToDictionary(x => x.ProductId, x => (x.Quantity, x.AvgCost));
    }

    public async Task<decimal> GetQuantityAsync(int productId, int warehouseId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var b = await db.StockBalances.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == productId && x.WarehouseId == warehouseId, ct);
        return b?.Quantity ?? 0;
    }

    public async Task<StockTransfer> TransferAsync(int fromWarehouseId, int toWarehouseId, IReadOnlyList<TransferLine> lines,
        string? comment = null, DateTime? date = null, CancellationToken ct = default)
    {
        Access.Demand(Perm.WarehouseTransfer);
        if (fromWarehouseId == toWarehouseId) throw new BusinessException("Склады отправителя и получателя должны различаться.");
        // Повторяющиеся товары сливаем в одну строку
        var valid = lines.Where(l => l.Quantity > 0)
            .GroupBy(l => l.ProductId)
            .Select(g => new TransferLine(g.Key, g.Sum(x => x.Quantity)))
            .ToList();
        if (valid.Count == 0) throw new BusinessException("Добавьте товары для перемещения.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var found = await db.Warehouses.CountAsync(w => w.Id == fromWarehouseId || w.Id == toWarehouseId, ct);
        if (found != 2) throw new BusinessException("Склад не найден.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var docDate = date ?? DateTime.Now;
        if (docDate.Date > DateTime.Today) throw new BusinessException("Дата перемещения не может быть в будущем.");
        var doc = new StockTransfer
        {
            Number = await DocumentNumbers.NextAsync(db, DocumentNumbers.Transfer, ct),
            Date = docDate, FromWarehouseId = fromWarehouseId, ToWarehouseId = toWarehouseId, Comment = comment?.Trim()
        };
        db.StockTransfers.Add(doc);
        await db.SaveChangesAsync(ct);

        foreach (var line in valid)
        {
            await EnsureGoodsAsync(db, line.ProductId, ct);
            var qty = Money.RoundQty(line.Quantity);
            var cost = await StockLedger.IssueAsync(db, line.ProductId, fromWarehouseId, qty, StockMovementType.TransferOut,
                doc.Id, doc.Number, docDate, allowNegative: false, ct);
            await StockLedger.ReceiveAsync(db, line.ProductId, toWarehouseId, qty, cost, StockMovementType.TransferIn,
                doc.Id, doc.Number, docDate, ct);
            doc.Items.Add(new StockTransferItem { ProductId = line.ProductId, Quantity = qty, UnitCost = cost });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return doc;
    }

    /// <summary>Инвентаризация: фактические остатки по указанным товарам, расхождения проводятся корректировкой.</summary>
    public async Task<InventoryCount> CreateInventoryAsync(int warehouseId, IReadOnlyList<InventoryLine> lines,
        string? comment = null, CancellationToken ct = default)
    {
        Access.Demand(Perm.WarehouseInventory);
        if (lines.Count == 0) throw new BusinessException("Нет позиций для инвентаризации.");
        if (lines.Any(l => l.ActualQuantity < 0)) throw new BusinessException("Фактический остаток не может быть отрицательным.");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Warehouses.AnyAsync(w => w.Id == warehouseId, ct)) throw new BusinessException("Склад не найден.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var date = DateTime.Now;
        var doc = new InventoryCount
        {
            Number = await DocumentNumbers.NextAsync(db, DocumentNumbers.Inventory, ct),
            Date = date, WarehouseId = warehouseId, Comment = comment?.Trim()
        };
        db.InventoryCounts.Add(doc);
        await db.SaveChangesAsync(ct);

        var distinct = lines.GroupBy(l => l.ProductId).Select(g => g.Last()).ToList();
        var ids = distinct.Select(l => l.ProductId).ToList();
        // Закупочные цены одним запросом — для товаров без себестоимости
        var purchasePrices = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.PurchasePrice }).ToDictionaryAsync(p => p.Id, p => p.PurchasePrice, ct);

        foreach (var line in distinct)
        {
            await EnsureGoodsAsync(db, line.ProductId, ct);
            var balance = await StockLedger.GetOrCreateAsync(db, line.ProductId, warehouseId, ct);
            var book = balance.Quantity;
            var actual = Money.RoundQty(line.ActualQuantity);
            var diff = actual - book;
            var cost = balance.AvgCost;
            if (cost == 0) cost = purchasePrices.GetValueOrDefault(line.ProductId);

            if (diff > 0)
                await StockLedger.ReceiveAsync(db, line.ProductId, warehouseId, diff, cost, StockMovementType.InventoryAdjust, doc.Id, doc.Number, date, ct);
            else if (diff < 0)
                // Недостача оценивается по фактической себестоимости списания (по FIFO — по партиям, а не по средней)
                cost = await StockLedger.IssueAsync(db, line.ProductId, warehouseId, -diff, StockMovementType.InventoryAdjust, doc.Id, doc.Number, date, true, ct);

            doc.Items.Add(new InventoryCountItem
            {
                ProductId = line.ProductId, BookQuantity = book, ActualQuantity = actual, Difference = diff, UnitCost = cost
            });
        }
        doc.DifferenceAmount = Money.Round(doc.Items.Sum(i => i.Difference * i.UnitCost));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return doc;
    }

    public async Task<List<StockTransfer>> GetTransfersAsync(DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.StockTransfers.AsNoTracking().Include(t => t.FromWarehouse).Include(t => t.ToWarehouse)
            .Include(t => t.Items).AsQueryable();
        if (from is not null) { var start = from.Value.Date; q = q.Where(t => t.Date >= start); }
        if (to is not null) { var end = to.Value.Date.AddDays(1); q = q.Where(t => t.Date < end); }
        return await q.OrderByDescending(t => t.Date).Take(from is null && to is null ? 500 : 5000).ToListAsync(ct);
    }

    public async Task<List<StockTransferItem>> GetTransferItemsAsync(int transferId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Set<StockTransferItem>().AsNoTracking().Include(i => i.Product)
            .Where(i => i.StockTransferId == transferId).ToListAsync(ct);
    }

    public async Task<List<InventoryCount>> GetInventoriesAsync(DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.InventoryCounts.AsNoTracking().Include(t => t.Warehouse).AsQueryable();
        if (from is not null) { var start = from.Value.Date; q = q.Where(t => t.Date >= start); }
        if (to is not null) { var end = to.Value.Date.AddDays(1); q = q.Where(t => t.Date < end); }
        return await q.OrderByDescending(t => t.Date).Take(from is null && to is null ? 500 : 5000).ToListAsync(ct);
    }

    /// <summary>Позиции проведённой инвентаризации (по умолчанию — только с расхождениями).</summary>
    public async Task<List<InventoryCountItem>> GetInventoryItemsAsync(int inventoryId, bool onlyDifferences = true, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var items = await db.Set<InventoryCountItem>().AsNoTracking().Include(i => i.Product)
            .Where(i => i.InventoryCountId == inventoryId).ToListAsync(ct);
        return items.Where(i => !onlyDifferences || i.Difference != 0)
            .OrderBy(i => i.Product?.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Списание товара со склада по себестоимости (порча, брак, истёк срок и т.п.).</summary>
    public async Task<StockWriteOff> WriteOffAsync(int warehouseId, IReadOnlyList<TransferLine> lines, string? reason,
        string? comment = null, DateTime? date = null, int? userId = null, CancellationToken ct = default)
    {
        Access.Demand(Perm.WarehouseWriteOff);
        var valid = lines.Where(l => l.Quantity > 0)
            .GroupBy(l => l.ProductId)
            .Select(g => new TransferLine(g.Key, g.Sum(x => x.Quantity)))
            .ToList();
        if (valid.Count == 0) throw new BusinessException("Добавьте товары для списания.");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Warehouses.AnyAsync(w => w.Id == warehouseId, ct)) throw new BusinessException("Склад не найден.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var docDate = date ?? DateTime.Now;
        if (docDate.Date > DateTime.Today) throw new BusinessException("Дата списания не может быть в будущем.");
        var doc = new StockWriteOff
        {
            Number = await DocumentNumbers.NextAsync(db, DocumentNumbers.WriteOff, ct),
            Date = docDate, WarehouseId = warehouseId, Reason = reason?.Trim(), Comment = comment?.Trim(), UserId = userId
        };
        db.StockWriteOffs.Add(doc);
        await db.SaveChangesAsync(ct);

        foreach (var line in valid)
        {
            await EnsureGoodsAsync(db, line.ProductId, ct);
            var qty = Money.RoundQty(line.Quantity);
            var cost = await StockLedger.IssueAsync(db, line.ProductId, warehouseId, qty, StockMovementType.WriteOff,
                doc.Id, doc.Number, docDate, allowNegative: false, ct);
            doc.Items.Add(new StockWriteOffItem { ProductId = line.ProductId, Quantity = qty, UnitCost = cost, Amount = Money.Round(qty * cost) });
        }
        doc.CostTotal = doc.Items.Sum(i => i.Amount);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return doc;
    }

    public async Task<List<StockWriteOff>> GetWriteOffsAsync(DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.StockWriteOffs.AsNoTracking().Include(t => t.Warehouse).Include(t => t.Items).AsQueryable();
        if (from is not null) { var start = from.Value.Date; q = q.Where(t => t.Date >= start); }
        if (to is not null) { var end = to.Value.Date.AddDays(1); q = q.Where(t => t.Date < end); }
        return await q.OrderByDescending(t => t.Date).Take(from is null && to is null ? 500 : 5000).ToListAsync(ct);
    }

    public async Task<List<StockWriteOffItem>> GetWriteOffItemsAsync(int writeOffId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Set<StockWriteOffItem>().AsNoTracking().Include(i => i.Product)
            .Where(i => i.StockWriteOffId == writeOffId).ToListAsync(ct);
    }

    public async Task<List<StockMovement>> GetMovementsAsync(DateTime from, DateTime to, int? productId = null,
        int? warehouseId = null, StockMovementType? type = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.StockMovements.AsNoTracking().Include(m => m.Product).Include(m => m.Warehouse)
            .Where(m => m.Date >= from.Date && m.Date < to.Date.AddDays(1));
        if (productId is not null) q = q.Where(m => m.ProductId == productId);
        if (warehouseId is not null) q = q.Where(m => m.WarehouseId == warehouseId);
        if (type is not null) q = q.Where(m => m.Type == type);
        return await q.OrderByDescending(m => m.Date).ThenByDescending(m => m.Id).Take(5000).ToListAsync(ct);
    }

    private static async Task EnsureGoodsAsync(TradeDbContext db, int productId, CancellationToken ct)
    {
        if (await db.Products.FindAsync([productId], ct) is { IsService: true } p)
            throw new BusinessException("«{0}» — услуга, она не учитывается на складе.", p.Name);
    }
}
