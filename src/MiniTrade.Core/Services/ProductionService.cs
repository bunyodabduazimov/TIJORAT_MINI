using MiniTrade.Core.Common;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Services;

public record MaterialLine(int ProductId, decimal Quantity);

public record ProductionRequest(int WarehouseId, int ProductId, decimal Quantity, IReadOnlyList<MaterialLine> Materials,
    decimal ExtraCost = 0, string? Comment = null, DateTime? Date = null, int? RecipeId = null);

/// <summary>Производство: техкарты и документы выпуска продукции (списание сырья → приход готовой продукции).</summary>
public class ProductionService(IDbContextFactory<TradeDbContext> factory, UserSession session)
{
    // ================= Техкарты =================

    public async Task<List<Recipe>> GetRecipesAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Recipes.AsNoTracking().Include(r => r.Product).Include(r => r.Items).ThenInclude(i => i.Product).AsQueryable();
        if (activeOnly) q = q.Where(r => r.IsActive);
        var list = await q.ToListAsync(ct);
        return list.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Создаёт или обновляет техкарту вместе с составом.</summary>
    public async Task<Recipe> SaveRecipeAsync(Recipe recipe, IReadOnlyList<MaterialLine> items, CancellationToken ct = default)
    {
        Access.Demand(Perm.ProductionRecipes);
        var name = recipe.Name?.Trim() ?? "";
        if (name.Length == 0) throw new BusinessException("Укажите название техкарты.");
        if (recipe.OutputQuantity <= 0) throw new BusinessException("Выход продукции должен быть больше нуля.");
        var lines = Merge(items);
        if (lines.Count == 0) throw new BusinessException("Добавьте материалы в техкарту.");
        if (lines.Any(l => l.ProductId == recipe.ProductId))
            throw new BusinessException("Продукция не может быть материалом самой себя.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var product = await db.Products.FindAsync([recipe.ProductId], ct) ?? throw new BusinessException("Выберите продукцию.");
        if (product.IsService) throw new BusinessException("«{0}» — услуга, её нельзя производить.", product.Name);

        Recipe entity;
        if (recipe.Id == 0)
        {
            entity = new Recipe();
            db.Recipes.Add(entity);
        }
        else
        {
            entity = await db.Recipes.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == recipe.Id, ct)
                     ?? throw new BusinessException("Техкарта не найдена.");
            db.RemoveRange(entity.Items);
            entity.Items.Clear();
        }
        entity.Name = name;
        entity.ProductId = recipe.ProductId;
        entity.OutputQuantity = Money.RoundQty(recipe.OutputQuantity);
        entity.Comment = string.IsNullOrWhiteSpace(recipe.Comment) ? null : recipe.Comment.Trim();
        entity.IsActive = recipe.IsActive;
        foreach (var l in lines)
            entity.Items.Add(new RecipeItem { ProductId = l.ProductId, Quantity = Money.RoundQty(l.Quantity) });
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task DeleteRecipeAsync(int id, CancellationToken ct = default)
    {
        Access.Demand(Perm.ProductionRecipes);
        await using var db = await factory.CreateDbContextAsync(ct);
        var recipe = await db.Recipes.FindAsync([id], ct);
        if (recipe is null) return;
        db.Recipes.Remove(recipe); // ссылки в документах обнуляются (SetNull), состав удаляется каскадно
        await db.SaveChangesAsync(ct);
    }

    // ================= Документы производства =================

    public async Task<List<ProductionOrder>> GetOrdersAsync(DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.ProductionOrders.AsNoTracking().Include(o => o.Warehouse).Include(o => o.Product).Include(o => o.Recipe).AsQueryable();
        if (from is not null) { var start = from.Value.Date; q = q.Where(o => o.Date >= start); }
        if (to is not null) { var end = to.Value.Date.AddDays(1); q = q.Where(o => o.Date < end); }
        return await q.OrderByDescending(o => o.Date).Take(from is null && to is null ? 500 : 5000).ToListAsync(ct);
    }

    public async Task<List<ProductionItem>> GetOrderItemsAsync(int orderId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ProductionItems.AsNoTracking().Include(i => i.Product)
            .Where(i => i.ProductionOrderId == orderId).ToListAsync(ct);
    }

    /// <summary>Проводит выпуск: материалы списываются со склада, продукция приходуется по фактической себестоимости.</summary>
    public async Task<ProductionOrder> ProduceAsync(ProductionRequest request, CancellationToken ct = default)
    {
        Access.Demand(Perm.ProductionProduce);
        if (request.Quantity <= 0) throw new BusinessException("Количество продукции должно быть больше нуля.");
        if (request.ExtraCost < 0) throw new BusinessException("Дополнительные расходы не могут быть отрицательными.");
        var materials = Merge(request.Materials);
        if (materials.Count == 0) throw new BusinessException("Добавьте материалы для производства.");
        if (materials.Any(m => m.ProductId == request.ProductId))
            throw new BusinessException("Продукция не может быть материалом самой себя.");
        var docDate = request.Date ?? DateTime.Now;
        if (docDate.Date > DateTime.Today) throw new BusinessException("Дата документа не может быть в будущем.");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Warehouses.AnyAsync(w => w.Id == request.WarehouseId, ct)) throw new BusinessException("Склад не найден.");
        var product = await db.Products.FindAsync([request.ProductId], ct) ?? throw new BusinessException("Выберите продукцию.");
        if (product.IsService) throw new BusinessException("«{0}» — услуга, её нельзя производить.", product.Name);
        var settings = await db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var allowNegative = settings?.AllowNegativeStock ?? false;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var doc = new ProductionOrder
        {
            Number = await DocumentNumbers.NextAsync(db, DocumentNumbers.Production, ct),
            Date = docDate, WarehouseId = request.WarehouseId, RecipeId = request.RecipeId, ProductId = request.ProductId,
            Quantity = Money.RoundQty(request.Quantity), ExtraCost = Money.Round(request.ExtraCost),
            Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim(), UserId = session.UserId
        };
        db.ProductionOrders.Add(doc);
        await db.SaveChangesAsync(ct);

        foreach (var m in materials)
        {
            var qty = Money.RoundQty(m.Quantity);
            var cost = await StockLedger.IssueAsync(db, m.ProductId, doc.WarehouseId, qty, StockMovementType.ProductionConsume,
                doc.Id, doc.Number, docDate, allowNegative, ct);
            doc.Items.Add(new ProductionItem { ProductId = m.ProductId, Quantity = qty, UnitCost = cost, Amount = Money.Round(qty * cost) });
        }
        doc.MaterialsCost = doc.Items.Sum(i => i.Amount);
        doc.TotalCost = Money.Round(doc.MaterialsCost + doc.ExtraCost);
        doc.UnitCost = Money.RoundCost(doc.TotalCost / doc.Quantity);
        await StockLedger.ReceiveAsync(db, doc.ProductId, doc.WarehouseId, doc.Quantity, doc.UnitCost,
            StockMovementType.ProductionOutput, doc.Id, doc.Number, docDate, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return doc;
    }

    /// <summary>Отмена документа: продукция снимается со склада, материалы возвращаются в свои партии.</summary>
    public async Task CancelAsync(int orderId, CancellationToken ct = default)
    {
        Access.Demand(Perm.ProductionCancel);
        await using var db = await factory.CreateDbContextAsync(ct);
        var doc = await db.ProductionOrders.Include(o => o.Items).Include(o => o.Product)
                      .FirstOrDefaultAsync(o => o.Id == orderId, ct)
                  ?? throw new BusinessException("Документ не найден.");
        var settings = await db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var balance = await StockLedger.GetOrCreateAsync(db, doc.ProductId, doc.WarehouseId, ct);
        if (!(settings?.AllowNegativeStock ?? false) && balance.Quantity < doc.Quantity)
            throw new BusinessException("Нельзя отменить: продукции «{0}» на складе {1:0.###}, выпущено {2:0.###}.",
                doc.Product?.Name, balance.Quantity, doc.Quantity);

        await StockLedger.RemoveReceiptAsync(db, doc.ProductId, doc.WarehouseId, doc.Quantity, doc.UnitCost,
            StockMovementType.ProductionOutput, doc.Id, ct);
        foreach (var item in doc.Items)
            await StockLedger.RestoreIssueAsync(db, item.ProductId, doc.WarehouseId, item.Quantity, item.UnitCost,
                doc.Date, StockMovementType.ProductionConsume, doc.Id, ct);

        db.StockMovements.RemoveRange(await db.StockMovements
            .Where(m => m.DocumentId == doc.Id && (m.Type == StockMovementType.ProductionOutput || m.Type == StockMovementType.ProductionConsume))
            .ToListAsync(ct));
        db.ProductionOrders.Remove(doc);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static List<MaterialLine> Merge(IEnumerable<MaterialLine> lines) => lines
        .Where(l => l.Quantity > 0)
        .GroupBy(l => l.ProductId)
        .Select(g => new MaterialLine(g.Key, g.Sum(x => x.Quantity)))
        .ToList();
}
