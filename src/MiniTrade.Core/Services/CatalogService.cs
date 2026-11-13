using MiniTrade.Core.Common;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Services;

/// <summary>Страница выборки и общее число записей (для постраничного вывода).</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

/// <summary>Универсальная работа со справочниками (товары, клиенты, поставщики, категории, склады, кассы).</summary>
public class CatalogService(IDbContextFactory<TradeDbContext> factory)
{
    public async Task<List<T>> GetAllAsync<T>(bool activeOnly = false, CancellationToken ct = default) where T : class, ICatalogEntity
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        IQueryable<T> q = db.Set<T>().AsNoTracking();
        if (typeof(T) == typeof(Product)) q = (IQueryable<T>)db.Products.AsNoTracking().Include(p => p.Category);
        if (typeof(T) == typeof(Counterparty)) q = (IQueryable<T>)db.Counterparties.AsNoTracking().Include(c => c.Group);
        q = q.Where(x => !x.IsDeleted);
        if (activeOnly) q = q.Where(x => x.IsActive);
        return (await q.ToListAsync(ct)).OrderBy(x => x.Name).ToList();
    }

    /// <summary>
    /// Страница справочника для таблицы: поиск и постраничная выборка выполняются в базе,
    /// поэтому даже десятки тысяч товаров не грузятся в память целиком.
    /// </summary>
    public async Task<PagedResult<T>> GetPageAsync<T>(string? search, bool includeInactive, int skip, int take,
        CancellationToken ct = default) where T : class, ICatalogEntity
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        IQueryable<T> q = typeof(T) == typeof(Product) ? (IQueryable<T>)db.Products.AsNoTracking().Include(p => p.Category)
            : typeof(T) == typeof(Counterparty) ? (IQueryable<T>)db.Counterparties.AsNoTracking().Include(c => c.Group)
            : db.Set<T>().AsNoTracking();
        q = q.Where(x => !x.IsDeleted);
        if (!includeInactive) q = q.Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(search)) q = ApplySearch(q, search.Trim());

        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(x => x.Name).ThenBy(x => x.Id).Skip(Math.Max(0, skip)).Take(Math.Max(1, take)).ToListAsync(ct);

        // Миниатюры фото — только для товаров текущей страницы
        if (items is List<Product> products && products.Count > 0)
        {
            var ids = products.Select(p => p.Id).ToList();
            var thumbs = await db.ProductPhotos.AsNoTracking().Where(p => ids.Contains(p.ProductId))
                .Select(p => new { p.ProductId, p.Thumbnail }).ToDictionaryAsync(p => p.ProductId, p => p.Thumbnail, ct);
            foreach (var p in products) p.Thumbnail = thumbs.GetValueOrDefault(p.Id);
        }

        // Баланс контрагентов текущей страницы:
        // − неоплаченные закупки (наш долг) ± прочие платежи (выдали контрагенту — он должен нам, получили — наоборот)
        if (items is List<Counterparty> parties && parties.Count > 0)
        {
            var ids = parties.Select(c => c.Id).ToList();
            var debts = (await db.Purchases.AsNoTracking().Where(p => ids.Contains(p.SupplierId))
                    .Select(p => new { p.SupplierId, p.Total, p.PaidAmount }).ToListAsync(ct))
                .GroupBy(p => p.SupplierId).ToDictionary(g => g.Key, g => g.Sum(p => p.Total - p.PaidAmount));
            // Продажи в долг: покупатель должен нам
            var credit = (await db.Sales.AsNoTracking().Where(s => s.CustomerId != null && ids.Contains(s.CustomerId.Value))
                    .Select(s => new { Id = s.CustomerId!.Value, s.Total, s.PaidAmount }).ToListAsync(ct))
                .GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.Sum(s => s.Total - s.PaidAmount));
            var payments = (await db.CashTransactions.AsNoTracking()
                    .Where(t => (t.Kind == CashOperationKind.CounterpartyPayment || t.Kind == CashOperationKind.CounterpartyAccrual) && t.CounterpartyId != null && ids.Contains(t.CounterpartyId.Value))
                    .Select(t => new { Id = t.CounterpartyId!.Value, t.Direction, t.Amount }).ToListAsync(ct))
                .GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.Sum(t => t.Direction == CashDirection.Expense ? t.Amount : -t.Amount));
            foreach (var c in parties)
                c.Balance = Money.Round(payments.GetValueOrDefault(c.Id) + credit.GetValueOrDefault(c.Id) - debts.GetValueOrDefault(c.Id));
        }
        return new PagedResult<T>(items, total);
    }

    /// <summary>Миниатюры фото всех товаров (Id товара → JPEG) — для плиток кассы.</summary>
    public async Task<Dictionary<int, byte[]>> GetProductThumbnailsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ProductPhotos.AsNoTracking().ToDictionaryAsync(p => p.ProductId, p => p.Thumbnail, ct);
    }

    /// <summary>Фото товара в полном размере (null — фото нет).</summary>
    public async Task<byte[]?> GetPhotoAsync(int productId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ProductPhotos.AsNoTracking().Where(p => p.ProductId == productId).Select(p => p.Data).FirstOrDefaultAsync(ct);
    }

    /// <summary>Сохраняет или удаляет (data = null) фото товара.</summary>
    public async Task SetPhotoAsync(int productId, byte[]? data, byte[]? thumbnail, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var photo = await db.ProductPhotos.FirstOrDefaultAsync(p => p.ProductId == productId, ct);
        if (data is null || data.Length == 0)
        {
            if (photo is not null) db.ProductPhotos.Remove(photo);
        }
        else
        {
            if (photo is null) db.ProductPhotos.Add(photo = new ProductPhoto { ProductId = productId });
            photo.Data = data;
            photo.Thumbnail = thumbnail ?? data;
            photo.UpdatedAt = DateTime.Now;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Поиск по подстроке. SQLite сравнивает кириллицу с учётом регистра, поэтому ищем
    /// и как введено, и строчными, и с заглавной первой буквой («кола» найдёт «Кола»).
    /// </summary>
    private static IQueryable<T> ApplySearch<T>(IQueryable<T> q, string term) where T : class, ICatalogEntity
    {
        var lower = term.ToLowerInvariant();
        var upper = term.ToUpperInvariant();
        var title = char.ToUpperInvariant(lower[0]) + lower[1..];
        return q switch
        {
            IQueryable<Product> p => (IQueryable<T>)p.Where(x =>
                x.Name.Contains(term) || x.Name.Contains(lower) || x.Name.Contains(title) || x.Name.Contains(upper)
                || (x.Sku != null && (x.Sku.Contains(term) || x.Sku.Contains(upper) || x.Sku.Contains(lower)))
                || (x.Barcode != null && x.Barcode.Contains(term))),
            IQueryable<Counterparty> s => (IQueryable<T>)s.Where(x =>
                x.Name.Contains(term) || x.Name.Contains(lower) || x.Name.Contains(title) || x.Name.Contains(upper)
                || (x.Phone != null && x.Phone.Contains(term))),
            _ => q.Where(x => x.Name.Contains(term) || x.Name.Contains(lower) || x.Name.Contains(title) || x.Name.Contains(upper))
        };
    }

    public async Task<T> SaveAsync<T>(T entity, CancellationToken ct = default) where T : class, ICatalogEntity
    {
        if (string.IsNullOrWhiteSpace(entity.Name)) throw new BusinessException("Наименование обязательно.");
        entity.Name = entity.Name.Trim();
        Validate(entity);

        await using var db = await factory.CreateDbContextAsync(ct);
        if (entity is Counterparty cp) cp.Group = null;
        if (entity is Product p) { p.Category = null; p.Thumbnail = null; } // навигация и миниатюра не сохраняются здесь
        if (entity.Id == 0)
        {
            if (entity is CashRegister cr && cr.Balance != 0)
                throw new BusinessException("Начальный остаток кассы вносится операцией «Приход» в разделе Финансы.");
            db.Set<T>().Add(entity);
        }
        else
        {
            if (entity is CashRegister cash)
            {
                // Остаток кассы меняется только через операции
                var existing = await db.CashRegisters.AsNoTracking().FirstAsync(c => c.Id == cash.Id, ct);
                cash.Balance = existing.Balance;
            }
            db.Set<T>().Update(entity);
        }
        await db.SaveChangesAsync(ct);
        return entity;
    }

    /// <summary>
    /// Мягкое удаление: запись помечается удалённой и неактивной, но остаётся в базе,
    /// поэтому документы, в которых она использована, не ломаются.
    /// </summary>
    /// <summary>Обновляет только цены продажи товаров (из строк закупки).</summary>
    public async Task SetSalePricesAsync(IReadOnlyDictionary<int, decimal> prices, CancellationToken ct = default)
    {
        if (prices.Count == 0) return;
        if (prices.Values.Any(p => p < 0)) throw new BusinessException("Цены не могут быть отрицательными.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = prices.Keys.ToList();
        foreach (var p in await db.Set<Product>().Where(x => ids.Contains(x.Id)).ToListAsync(ct))
            p.SalePrice = Money.Round(prices[p.Id]);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync<T>(int id, CancellationToken ct = default) where T : class, ICatalogEntity
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.Set<T>().FindAsync([id], ct);
        if (entity is null || entity.IsDeleted) return;
        if (entity is CashRegister { Balance: not 0 })
            throw new BusinessException("Нельзя удалить кассу с ненулевым остатком.");
        entity.IsDeleted = true;
        entity.IsActive = false;
        await db.SaveChangesAsync(ct);
    }

    private static void Validate(ICatalogEntity entity)
    {
        if (entity is Product p)
        {
            if (p.SalePrice < 0 || p.PurchasePrice < 0) throw new BusinessException("Цены не могут быть отрицательными.");
            if (p.MinStock < 0) throw new BusinessException("Минимальный остаток не может быть отрицательным.");
            if (string.IsNullOrWhiteSpace(p.Unit)) p.Unit = "шт";
            p.SalePrice = Money.Round(p.SalePrice);
            p.PurchasePrice = Money.Round(p.PurchasePrice);
        }
    }
}
