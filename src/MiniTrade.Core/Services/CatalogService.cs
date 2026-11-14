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
        CancellationToken ct = default, ProductFilter? productFilter = null) where T : class, ICatalogEntity
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        IQueryable<T> q = typeof(T) == typeof(Product) ? (IQueryable<T>)db.Products.AsNoTracking().Include(p => p.Category)
            : typeof(T) == typeof(Counterparty) ? (IQueryable<T>)db.Counterparties.AsNoTracking().Include(c => c.Group)
            : db.Set<T>().AsNoTracking();
        // Удалённые товары видны только через фильтр «Удалённые»
        if (productFilter is null || typeof(T) != typeof(Product)) q = q.Where(x => !x.IsDeleted);
        // У товаров архив задаётся в фильтре (IsActive), флажок «Показывать архив» — у остальных справочников
        if (productFilter is null && !includeInactive) q = q.Where(x => x.IsActive);
        if (productFilter is not null && q is IQueryable<Product> pq) q = (IQueryable<T>)ApplyProductFilter(db, pq, productFilter);
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

        // Сумма остатков складов текущей страницы: по себестоимости и по ценам продажи
        if (items is List<Warehouse> warehouses && warehouses.Count > 0)
        {
            var ids = warehouses.Select(w => w.Id).ToList();
            var rows = await db.StockBalances.AsNoTracking().Where(s => ids.Contains(s.WarehouseId) && s.Quantity > 0)
                .Select(s => new { s.WarehouseId, s.Quantity, s.AvgCost, s.Product!.SalePrice }).ToListAsync(ct);
            foreach (var w in warehouses)
            {
                var own = rows.Where(r => r.WarehouseId == w.Id).ToList();
                w.StockCost = Money.Round(own.Sum(r => r.Quantity * r.AvgCost));
                w.StockSaleValue = Money.Round(own.Sum(r => r.Quantity * r.SalePrice));
            }
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
        Access.Demand(Perm.Catalog(typeof(Product), "edit"));
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

    private static IQueryable<Product> ApplyProductFilter(TradeDbContext db, IQueryable<Product> q, ProductFilter f)
    {
        if (f.CategoryId == ProductFilter.NoCategory) q = q.Where(p => p.CategoryId == null);
        else if (f.CategoryId is { } cat) q = q.Where(p => p.CategoryId == cat);
        if (!string.IsNullOrEmpty(f.Unit)) q = q.Where(p => p.Unit == f.Unit);
        if (f.IsService is { } svc) q = q.Where(p => p.IsService == svc);
        if (f.IsWeighted is { } w) q = q.Where(p => p.IsWeighted == w);
        // Удалённые всегда неактивны — фильтр активности применяется только к неудалённым
        if (f.IsActive is { } a) q = q.Where(p => p.IsDeleted || p.IsActive == a);
        if (f.IsDeleted is { } del) q = q.Where(p => p.IsDeleted == del);
        if (f.HasPhoto is { } ph)
            q = ph ? q.Where(p => db.ProductPhotos.Any(x => x.ProductId == p.Id))
                   : q.Where(p => !db.ProductPhotos.Any(x => x.ProductId == p.Id));
        if (f.HasBarcode is { } bc)
            q = bc ? q.Where(p => p.Barcode != null && p.Barcode != "") : q.Where(p => p.Barcode == null || p.Barcode == "");
        return q;
    }

    public async Task<T> SaveAsync<T>(T entity, CancellationToken ct = default) where T : class, ICatalogEntity
    {
        Access.Demand(Perm.Catalog(typeof(T), entity.Id == 0 ? "create" : "edit"));
        if (string.IsNullOrWhiteSpace(entity.Name)) throw new BusinessException("Наименование обязательно.");
        entity.Name = entity.Name.Trim();
        Validate(entity);

        await using var db = await factory.CreateDbContextAsync(ct);
        if (entity is Counterparty cp) cp.Group = null;
        if (entity is Product p) { p.Category = null; p.Thumbnail = null; } // навигация и миниатюра не сохраняются здесь
        if (entity is MeasureUnit unit)
        {
            if (unit.Name.Length > 16) throw new BusinessException("Краткое наименование единицы — не длиннее 16 символов.");
            var others = await db.MeasureUnits.AsNoTracking().Where(u => !u.IsDeleted && u.Id != unit.Id).Select(u => u.Name).ToListAsync(ct);
            if (others.Any(n => string.Equals(n, unit.Name, StringComparison.CurrentCultureIgnoreCase)))
                throw new BusinessException("Единица «{0}» уже есть в справочнике.", unit.Name);
            if (unit.Id != 0)
            {
                // Переименование: товары хранят краткое наименование — обновляем их тоже
                var oldName = await db.MeasureUnits.AsNoTracking().Where(u => u.Id == unit.Id).Select(u => u.Name).FirstAsync(ct);
                if (oldName != unit.Name)
                    foreach (var prod in await db.Products.Where(x => x.Unit == oldName).ToListAsync(ct))
                        prod.Unit = unit.Name;
            }
        }
        if (entity.Id == 0)
        {
            if (entity is CashRegister cr && cr.Balance != 0)
                throw new BusinessException("Начальный остаток кассы вносится операцией «Приход» в разделе Финансы.");
            db.Set<T>().Add(entity);
        }
        else
        {
            if (entity is Product { IsService: true } svc
                && await db.StockBalances.AnyAsync(b => b.ProductId == svc.Id && b.Quantity != 0, ct))
                throw new BusinessException("Нельзя сделать услугой товар, у которого есть остатки на складе.");
            if (entity is CashRegister cash)
            {
                // Остаток кассы меняется только через операции
                var existing = await db.CashRegisters.AsNoTracking().FirstAsync(c => c.Id == cash.Id, ct);
                cash.Balance = existing.Balance;
            }
            if (entity is Product prod)
            {
                // Дата изменения сдвигается, только если поменялось то, что уходит в весы
                var old = await db.Products.AsNoTracking().FirstAsync(x => x.Id == prod.Id, ct);
                var changed = old.Name != prod.Name || old.Barcode != prod.Barcode || old.SalePrice != prod.SalePrice
                    || old.IsWeighted != prod.IsWeighted || old.IsActive != prod.IsActive;
                prod.UpdatedAt = changed ? DateTime.Now : old.UpdatedAt;
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
        Access.Demand(Perm.CatalogProductsPrices);
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
        Access.Demand(Perm.Catalog(typeof(T), "delete"));
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
            if (p.IsService) p.MinStock = 0;
            if (p.MinStock < 0) throw new BusinessException("Минимальный остаток не может быть отрицательным.");
            if (string.IsNullOrWhiteSpace(p.Unit)) p.Unit = "шт";
            p.SalePrice = Money.Round(p.SalePrice);
            p.PurchasePrice = Money.Round(p.PurchasePrice);
        }
        if (entity is ScaleDevice s)
        {
            s.Ip = (s.Ip ?? "").Trim();
            s.FilePath = string.IsNullOrWhiteSpace(s.FilePath) ? null : s.FilePath.Trim();
            if (s.IsFileBased)
            {
                // Rongta: вместо сети — файл PLU для программы RLS1000
                s.FilePath ??= ScaleDevice.RongtaDefaultPath;
                return;
            }
            if (!System.Net.IPAddress.TryParse(s.Ip, out _)) throw new BusinessException("Укажите IP-адрес весов, например 192.168.1.50.");
            if (s.Port is < 1 or > 65535) throw new BusinessException("Порт весов — число от 1 до 65535.");
        }
    }

    /// <summary>
    /// Свободный штрихкод для товара. Весовой — наименьший свободный код весов 1…99999;
    /// обычный — внутренний EAN-13 (начинается с 2, не с кода весов) с контрольной цифрой.
    /// </summary>
    public async Task<string> GenerateBarcodeAsync(bool weighted, int productId = 0, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var used = (await db.Products.AsNoTracking().Where(p => p.Id != productId && p.Barcode != null && p.Barcode != "")
            .Select(p => p.Barcode!).ToListAsync(ct)).Select(b => b.Trim()).ToHashSet();

        if (weighted)
        {
            var codes = used.Where(b => b.Length <= 5 && b.All(char.IsAsciiDigit)).Select(int.Parse).ToHashSet();
            for (var c = 1; c <= 99999; c++)
                if (!codes.Contains(c)) return c.ToString();
            throw new BusinessException("Свободных кодов весов (1–99999) не осталось.");
        }

        var scale = await db.CompanySettings.AsNoTracking().Select(x => x.ScalePrefix).FirstOrDefaultAsync(ct);
        // Внутренний диапазон 20–29, кроме кода весов
        var prefixes = Enumerable.Range(20, 10).Select(n => n.ToString()).Where(p => p != scale).ToArray();
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var body = prefixes[Random.Shared.Next(prefixes.Length)] + Random.Shared.NextInt64(0, 10_000_000_000).ToString("D10");
            var code = body + Ean13CheckDigit(body);
            if (!used.Contains(code)) return code;
        }
        throw new BusinessException("Не удалось подобрать свободный штрихкод.");
    }

    private static int Ean13CheckDigit(string twelve)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (twelve[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10;
    }
}

/// <summary>Фильтр таблицы товаров; null в поле — «все».</summary>
/// <remarks>IsActive: true — активные (по умолчанию), null — все, false — неактивные. IsDeleted: false — без удалённых (по умолчанию), null — с удалёнными, true — только удалённые.</remarks>
public sealed record ProductFilter(int? CategoryId = null, string? Unit = null, bool? IsService = null, bool? IsWeighted = null,
    bool? IsActive = true, bool? HasPhoto = null, bool? HasBarcode = null, bool? IsDeleted = false)
{
    /// <summary>Значение CategoryId «без категории».</summary>
    public const int NoCategory = -1;

    public int ActiveCount =>
        (CategoryId is null ? 0 : 1) + (string.IsNullOrEmpty(Unit) ? 0 : 1) + (IsService is null ? 0 : 1) + (IsWeighted is null ? 0 : 1)
        + (IsActive is true ? 0 : 1) + (HasPhoto is null ? 0 : 1) + (HasBarcode is null ? 0 : 1) + (IsDeleted is false ? 0 : 1);
}
