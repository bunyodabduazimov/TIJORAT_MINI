using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Data;

/// <summary>
/// Нагрузочные тестовые данные: справочники вставляются пачками, документы — через бизнес-сервисы,
/// чтобы остатки, партии, кассы и долги были согласованы.
/// </summary>
public static class DemoDataGenerator
{
    public sealed record Options(int Products = 10_000, int Counterparties = 1_000, int Purchases = 1_000,
        int Sales = 1_000, int Payments = 1_000, int Days = 180, int Seed = 42);

    private static readonly string[] Words =
    [
        "Мука", "Сахар", "Рис", "Масло", "Чай", "Кофе", "Соль", "Макароны", "Гречка", "Молоко",
        "Сок", "Вода", "Печенье", "Конфеты", "Мыло", "Шампунь", "Порошок", "Салфетки", "Батарейки", "Лампа",
        "Кабель", "Розетка", "Тетрадь", "Ручка", "Носки", "Футболка", "Кружка", "Тарелка", "Нож", "Ведро"
    ];
    private static readonly string[] Brands = ["Премиум", "Эконом", "Классик", "Голд", "Фреш", "Хит", "Люкс", "Стандарт"];
    private static readonly string[] Names = ["Алиев", "Каримов", "Рахимов", "Саидов", "Назаров", "Шарипов", "Юсупов", "Холов", "Мирзоев", "Одинаев"];
    private static readonly string[] Firms = ["ООО «Бахор»", "ООО «Сомон»", "ИП «Варзоб»", "ООО «Памир»", "ООО «Хатлон»", "ИП «Зарафшон»"];

    public static async Task RunAsync(IDbContextFactory<TradeDbContext> factory, UserSession session,
        Options? o = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        o ??= new Options();
        var rnd = new Random(o.Seed);
        var stamp = DateTime.Now.ToString("HHmmss");

        int warehouseId, cashId;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            warehouseId = await db.Warehouses.Where(w => !w.IsDeleted).Select(w => w.Id).FirstAsync(ct);
            cashId = await db.CashRegisters.Select(c => c.Id).FirstAsync(ct);
            db.ChangeTracker.AutoDetectChangesEnabled = false;

            // Справочники от прошлого (прерванного) запуска — используются повторно
            var oldSku = await db.Products.AsNoTracking().Where(p => EF.Functions.Like(p.Sku!, "T______-_____"))
                .Select(p => p.Sku!).FirstOrDefaultAsync(ct);
            if (oldSku != null)
            {
                stamp = oldSku[1..7];
                progress?.Report("Справочники уже есть — используются повторно");
                goto catalogDone;
            }

            // Категории
            var cats = Words.Select(w => new Category { Name = $"{w} ({stamp})" }).ToList();
            db.Categories.AddRange(cats);
            await db.SaveChangesAsync(ct);

            // Товары
            for (int i = 0; i < o.Products; i += 1000)
            {
                for (int k = i; k < Math.Min(i + 1000, o.Products); k++)
                {
                    var c = rnd.Next(cats.Count);
                    var cost = Math.Round((decimal)(rnd.NextDouble() * 200 + 2), 2);
                    db.Products.Add(new Product
                    {
                        Name = $"{Words[c]} {Brands[rnd.Next(Brands.Length)]} №{k + 1}",
                        Sku = $"T{stamp}-{k + 1:D5}",
                        Barcode = $"2{stamp}{k + 1:D6}",
                        CategoryId = cats[c].Id,
                        PurchasePrice = cost,
                        SalePrice = Math.Round(cost * 1.3m, 2),
                        MinStock = rnd.Next(0, 10)
                    });
                }
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                progress?.Report($"Товары: {Math.Min(i + 1000, o.Products)}/{o.Products}");
            }

            // Контрагенты
            for (int k = 0; k < o.Counterparties; k++)
                db.Counterparties.Add(new Counterparty
                {
                    Name = k % 3 == 0 ? $"{Firms[rnd.Next(Firms.Length)]} {k + 1}" : $"{Names[rnd.Next(Names.Length)]} {k + 1}",
                    Phone = $"+992 9{rnd.Next(10)} {rnd.Next(100, 999)} {rnd.Next(1000, 9999)}",
                    Address = $"г. Душанбе, ул. Тестовая, {rnd.Next(1, 200)}"
                });
            await db.SaveChangesAsync(ct);
            progress?.Report($"Контрагенты: {o.Counterparties}");
            catalogDone:;
        }

        List<(int Id, decimal Cost, decimal Price)> products;
        List<int> parties;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            products = (await db.Products.AsNoTracking().Where(p => p.Sku!.StartsWith("T" + stamp))
                .Select(p => new { p.Id, p.PurchasePrice, p.SalePrice }).ToListAsync(ct))
                .Select(p => (p.Id, p.PurchasePrice, p.SalePrice)).ToList();
            parties = await db.Counterparties.AsNoTracking().Where(c => c.Address!.StartsWith("г. Душанбе, ул. Тестовая"))
                .OrderByDescending(c => c.Id).Take(o.Counterparties).Select(c => c.Id).ToListAsync(ct);
        }

        // Документы в хронологическом порядке: сначала закупка, потом продажи из того, что есть на складе
        var start = DateTime.Today.AddDays(-o.Days);
        DateTime DateAt(int i, int n) => start.AddMinutes((double)o.Days * 24 * 60 * i / n + rnd.Next(0, 30));

        var purchases = new PurchaseService(factory, session);
        // Касса пустая — закупки оплачиваются из начального капитала
        await new FinanceService(factory, session).AddOperationAsync(cashId, CashDirection.Income, "Начальный капитал",
            100_000_000m, "Тестовые данные", start.AddDays(-1), ct);
        var sales = new SalesService(factory, session);
        var finance = new FinanceService(factory, session);
        var stock = new Dictionary<int, decimal>();
        var stocked = new List<int>(); // индексы в products

        var events = new List<(DateTime Date, int Kind)>();
        for (int i = 0; i < o.Purchases; i++) events.Add((DateAt(i, o.Purchases).AddHours(-1), 0));
        for (int i = 0; i < o.Sales; i++) events.Add((DateAt(i, o.Sales), 1));
        for (int i = 0; i < o.Payments; i++) events.Add((DateAt(i, o.Payments).AddHours(1), 2));
        events.Sort((a, b) => a.Date.CompareTo(b.Date));

        int done = 0;
        foreach (var (date, kind) in events)
        {
            ct.ThrowIfCancellationRequested();
            switch (kind)
            {
                case 0: // Закупка: 5–25 позиций
                {
                    var lines = new List<DocumentLine>();
                    foreach (var idx in Pick(rnd, products.Count, rnd.Next(5, 26)))
                    {
                        var p = products[idx];
                        var qty = rnd.Next(10, 101);
                        lines.Add(new DocumentLine(p.Id, qty, p.Cost));
                        if (!stock.ContainsKey(p.Id)) stocked.Add(idx);
                        stock[p.Id] = stock.GetValueOrDefault(p.Id) + qty;
                    }
                    var total = lines.Sum(l => l.Quantity * l.Price);
                    var paid = rnd.Next(3) switch { 0 => 0, 1 => Math.Round(total / 2, 2), _ => total };
                    await purchases.CreatePurchaseAsync(new PurchaseRequest(parties[rnd.Next(parties.Count)], warehouseId, lines,
                        paid, cashId, "Тестовые данные", date, UpdateProductPurchasePrice: false), ct);
                    break;
                }
                case 1: // Продажа: 1–10 позиций из имеющихся
                {
                    if (stocked.Count == 0) break;
                    var lines = new List<DocumentLine>();
                    foreach (var j in Pick(rnd, stocked.Count, rnd.Next(1, 11)))
                    {
                        var p = products[stocked[j]];
                        var have = stock[p.Id];
                        if (have < 1) continue;
                        var qty = Math.Min(have, rnd.Next(1, 6));
                        lines.Add(new DocumentLine(p.Id, qty, p.Price));
                        stock[p.Id] = have - qty;
                    }
                    if (lines.Count == 0) break;
                    var credit = rnd.Next(5) == 0;
                    int? customer = credit || rnd.Next(2) == 0 ? parties[rnd.Next(parties.Count)] : null;
                    await sales.CreateSaleAsync(new SaleRequest(warehouseId, cashId, lines, customer, "Тестовые данные", date,
                        OnCredit: credit), ct);
                    break;
                }
                default: // Платёж контрагенту / от контрагента
                {
                    var income = rnd.Next(2) == 0;
                    await finance.CounterpartyPaymentAsync(new CounterpartyPaymentRequest(
                        income ? CashDirection.Income : CashDirection.Expense, cashId, parties[rnd.Next(parties.Count)],
                        Math.Round((decimal)(rnd.NextDouble() * 2000 + 50), 2), Comment: "Тестовые данные"), ct);
                    break;
                }
            }
            if (++done % 100 == 0) progress?.Report($"Документы: {done}/{events.Count}");
        }
        progress?.Report("Готово");
    }

    private static IEnumerable<int> Pick(Random rnd, int count, int n)
    {
        var set = new HashSet<int>();
        n = Math.Min(n, count);
        while (set.Count < n) set.Add(rnd.Next(count));
        return set;
    }
}
