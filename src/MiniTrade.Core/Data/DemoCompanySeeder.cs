using System.Text.RegularExpressions;
using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Data;

/// <summary>Ход заполнения демо-данными: доля 0…1 и описание текущего шага.</summary>
public sealed record DemoProgress(double Fraction, string Step);

/// <summary>
/// Демо-компания для знакомства с программой: реалистичный каталог выбранного направления, клиенты и поставщики,
/// и год работы — ежедневные продажи, закупки, оплаты, расходы, возвраты и списания. Все документы проводятся
/// через бизнес-сервисы, поэтому остатки, партии, кассы и долги согласованы.
/// </summary>
public static class DemoCompanySeeder
{
    private const int Days = 365;

    /// <summary>База пустая: нет товаров, контрагентов и документов (стартовые справочники не в счёт).</summary>
    public static async Task<bool> IsDatabaseEmptyAsync(IDbContextFactory<TradeDbContext> factory, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return !await db.Products.AnyAsync(ct) && !await db.Counterparties.AnyAsync(ct)
               && !await db.Sales.AnyAsync(ct) && !await db.Purchases.AnyAsync(ct) && !await db.CashTransactions.AnyAsync(ct);
    }

    public static async Task RunAsync(IDbContextFactory<TradeDbContext> factory, UserSession session, DemoIndustry industry,
        IProgress<DemoProgress>? progress = null, CancellationToken ct = default, int? seed = null)
    {
        var profile = DemoIndustries.Get(industry);
        var run = new Run(factory, session, profile, new Random(seed ?? Environment.TickCount), progress, ct);
        await run.CreateCatalogAsync();
        await run.SimulateAsync();
        progress?.Report(new DemoProgress(1, "Готово"));
    }

    private sealed class Prod
    {
        public int Id;
        public string Unit = "шт";
        public decimal Cost, Price;
        public double Weight;           // популярность
        public int Supplier;            // индекс в списке поставщиков; -1 — не закупается (услуга или своя продукция)
        public bool IsService;          // услуга: без остатков и закупок
        public decimal Stock;
        public decimal Sold28;          // продано за последние 4 недели (скользящее окно)
        public decimal MinLot;          // минимальная партия закупки
        public decimal Demand;          // ожидаемый расход в день (до появления статистики продаж)
        public decimal Daily => Sold28 > 0 ? Sold28 / 28 : Demand;
    }

    private sealed class Party
    {
        public int Id;
        public bool Wholesale;
        public decimal Debt;            // нам должны
    }

    private sealed class Run(IDbContextFactory<TradeDbContext> factory, UserSession session, DemoProfile profile, Random rnd,
        IProgress<DemoProgress>? progress, CancellationToken ct)
    {
        private readonly List<Prod> _products = [];
        private readonly List<Party> _customers = [];
        private readonly List<Batch> _recipes = [];
        private readonly List<int> _suppliers = [];
        private readonly Dictionary<int, decimal> _supplierDebt = [];
        private readonly SortedDictionary<DateTime, List<Func<Task>>> _scheduled = [];
        private double _totalWeight;
        private int _warehouseId, _cashId, _bankId;
        private decimal _cash, _bank, _monthRevenue, _lastMonthRevenue;

        // ───────────────────────────── Справочники ─────────────────────────────

        public async Task CreateCatalogAsync()
        {
            Report(0, "Реквизиты компании");
            await using var db = await factory.CreateDbContextAsync(ct);

            var company = await db.CompanySettings.FirstAsync(ct);
            company.Name = profile.CompanyName;
            company.Address = profile.Address;
            company.Phone = profile.Phone;
            company.TaxId ??= $"0{rnd.Next(10_000_000, 99_999_999)}";
            company.ReceiptFooter = profile.ReceiptFooter;

            _warehouseId = await db.Warehouses.Where(w => !w.IsDeleted).OrderBy(w => w.Id).Select(w => w.Id).FirstAsync(ct);
            var cash = await db.CashRegisters.OrderBy(c => c.Id).FirstAsync(ct);
            _cashId = cash.Id;
            var bank = new CashRegister { Name = "Расчётный счёт (банк, карты)", IsCashless = true };
            db.CashRegisters.Add(bank);

            // Единицы измерения каталога
            var units = await db.MeasureUnits.Select(u => u.Name).ToListAsync(ct);
            foreach (var (name, full) in new[] { ("мешок", "Мешок"), ("м", "Метр"), ("м²", "Квадратный метр"), ("л", "Литр"), ("усл", "Услуга") })
                if (!units.Contains(name)) db.MeasureUnits.Add(new MeasureUnit { Name = name, Description = full });

            // Категории и товары
            Report(0.01, "Каталог товаров");
            var all = new List<(DemoCategory Cat, string Name, string Unit, decimal Cost)>();
            foreach (var cat in profile.Categories)
                foreach (var item in cat.Items)
                {
                    // «шаблон|ед|цена»: в шаблоне тоже есть «|» (варианты в {}), поэтому разбираем с конца
                    var p2 = item.LastIndexOf('|');
                    var p1 = item.LastIndexOf('|', p2 - 1);
                    var (template, unit, cost) = (item[..p1], item[(p1 + 1)..p2],
                        decimal.Parse(item[(p2 + 1)..], System.Globalization.CultureInfo.InvariantCulture));
                    foreach (var (name, factor) in Expand(template))
                        all.Add((cat, name, unit, cost * factor));
                }
            // 200–400 товаров (или все, если вариантов меньше); каждая категория представлена
            var target = profile.IsManufacturing ? all.Count : Math.Min(all.Count, rnd.Next(200, 401));
            var chosen = all.OrderBy(_ => rnd.Next()).Take(target)
                .Concat(profile.Categories.Select(c => all.First(a => a.Cat == c)))
                .Distinct().OrderBy(a => Array.IndexOf(profile.Categories, a.Cat)).ThenBy(a => a.Name).ToList();

            var cats = profile.Categories.ToDictionary(c => c, c => new Category { Name = c.Name });
            db.Categories.AddRange(cats.Values);
            // Стартовая «Общая» категория не нужна, если пустая
            foreach (var general in await db.Categories.Where(c => c.Name == "Общая").ToListAsync(ct)) db.Categories.Remove(general);

            // Поставщики: каждая категория закреплена за одним поставщиком
            var groups = await db.CounterpartyGroups.ToListAsync(ct);
            var buyers = groups.FirstOrDefault(g => g.Name == "Покупатели") ?? Add(db, new CounterpartyGroup { Name = "Покупатели" });
            var vendors = groups.FirstOrDefault(g => g.Name == "Поставщики") ?? Add(db, new CounterpartyGroup { Name = "Поставщики" });
            var wholesale = Add(db, new CounterpartyGroup { Name = "Оптовые покупатели" });

            var supplierEntities = profile.Suppliers.Select(s => new Counterparty
            {
                Name = s, Group = vendors, Phone = Phone(true), Address = $"{DemoIndustries.Cities[rnd.Next(4)]}, {Street()}",
                Note = "Поставщик"
            }).ToList();
            db.Counterparties.AddRange(supplierEntities);

            var prods = new List<(Product Entity, Prod Info)>();
            int seq = 0;
            foreach (var (cat, name, unit, cost0) in chosen)
            {
                var isService = unit == "усл";
                var cost = RoundPrice(cost0 * (decimal)(0.92 + rnd.NextDouble() * 0.16));
                if (isService)
                {
                    // Для услуги в шаблоне — цена; себестоимость (работа мастера) в учёт не попадает
                    var service = new Product
                    {
                        Name = name, Unit = unit, Category = cats[cat], IsService = true,
                        Sku = $"{Abbrev(cat.Name)}-{++seq:D4}", PurchasePrice = 0, SalePrice = cost
                    };
                    prods.Add((service, new Prod
                    {
                        Unit = unit, Cost = 0, Price = cost, IsService = true, Supplier = -1, Stock = 1_000_000,
                        Weight = Math.Pow(rnd.NextDouble(), 2) * 3 + 1.5
                    }));
                    db.Products.Add(service);
                    continue;
                }
                var markup = profile.MarkupMin + (profile.MarkupMax - profile.MarkupMin) * (decimal)rnd.NextDouble();
                var price = RoundPrice(cost * (1 + markup));
                if (price <= cost) price = cost + Step(cost);
                var entity = new Product
                {
                    Name = name, Unit = unit, Category = cats[cat],
                    Sku = $"{Abbrev(cat.Name)}-{++seq:D4}",
                    Barcode = Ean13("488" + rnd.Next(1000, 9999).ToString() + seq.ToString("D5")),
                    PurchasePrice = cost, SalePrice = price
                };
                var info = new Prod
                {
                    Unit = unit, Cost = cost, Price = price,
                    // Дешёвые ходовые товары продаются чаще; у каждого — свой «спрос»
                    // Сырьё производства покупателям не продаётся — расходуется в цехе
                    Weight = profile.IsManufacturing ? 0 : Math.Pow(rnd.NextDouble(), 2) * 3 + 0.2 + (price < 50 ? 1.5 : price < 500 ? 0.6 : 0.1),
                    Supplier = Array.IndexOf(profile.Categories, cat) % profile.Suppliers.Length
                };
                info.MinLot = MinLot(info);
                prods.Add((entity, info));
                db.Products.Add(entity);
            }

            // Готовая продукция по техкартам: себестоимость = материалы + ~15% (работа, электроэнергия)
            var recipeDrafts = new List<(Product Entity, Prod Info, DemoRecipe Recipe, List<(Product Mat, Prod Info, decimal Qty)> Mats)>();
            foreach (var recipe in profile.Recipes ?? [])
            {
                var mats = recipe.Materials.Select(m =>
                {
                    var i = m.LastIndexOf('|');
                    var name = m[..i];
                    var mat = prods.FirstOrDefault(x => x.Entity.Name == name);
                    if (mat.Entity is null) throw new InvalidOperationException($"Демо: нет материала «{name}» для «{recipe.Product}».");
                    return (mat.Entity, mat.Info, decimal.Parse(m[(i + 1)..], System.Globalization.CultureInfo.InvariantCulture));
                }).ToList();
                var cost = RoundPrice(mats.Sum(m => m.Info.Cost * m.Item3) / recipe.Batch * 1.15m);
                var markup = profile.MarkupMin + (profile.MarkupMax - profile.MarkupMin) * (decimal)rnd.NextDouble();
                var price = RoundPrice(cost * (1 + markup));
                if (!cats.Keys.Any(k => k.Name == recipe.Category))
                {
                    var key = new DemoCategory(recipe.Category, []);
                    cats[key] = new Category { Name = recipe.Category };
                    db.Categories.Add(cats[key]);
                }
                var category = cats.First(c => c.Key.Name == recipe.Category).Value;
                var entity = new Product
                {
                    Name = recipe.Product, Unit = recipe.Unit, Category = category,
                    Sku = $"ГП-{++seq:D4}", Barcode = Ean13("488" + rnd.Next(1000, 9999).ToString() + seq.ToString("D5")),
                    PurchasePrice = cost, SalePrice = price
                };
                var info = new Prod
                {
                    Unit = recipe.Unit, Cost = cost, Price = price, Supplier = -1,
                    Weight = Math.Pow(rnd.NextDouble(), 2) * 3 + 0.5 + (price < 10 ? 3 : price < 100 ? 1 : 0.3)
                };
                prods.Add((entity, info));
                db.Products.Add(entity);
                recipeDrafts.Add((entity, info, recipe, mats.Select(m => (m.Entity, m.Info, m.Item3)).ToList()));
            }

            // Покупатели: 50–100, из них ~15% — организации (оптовики)
            Report(0.03, "Клиенты");
            var customerCount = rnd.Next(50, 101);
            var used = new HashSet<string>();
            var customerEntities = new List<(Counterparty, bool)>();
            foreach (var firm in profile.WholesaleCustomers)
                customerEntities.Add((new Counterparty
                {
                    Name = firm, Group = wholesale, Phone = Phone(true),
                    Address = $"{DemoIndustries.Cities[rnd.Next(DemoIndustries.Cities.Length)]}, {Street()}",
                    Note = "Оптовый клиент, отсрочка до 30 дней"
                }, true));
            while (customerEntities.Count < customerCount)
            {
                var female = rnd.Next(3) == 0;
                var surname = DemoIndustries.Surnames[rnd.Next(DemoIndustries.Surnames.Length)] + (female ? "а" : "");
                var first = female ? DemoIndustries.FemaleNames[rnd.Next(DemoIndustries.FemaleNames.Length)]
                                   : DemoIndustries.MaleNames[rnd.Next(DemoIndustries.MaleNames.Length)];
                var name = $"{surname} {first}";
                if (!used.Add(name)) continue;
                customerEntities.Add((new Counterparty
                {
                    Name = name, Group = buyers, Phone = Phone(false),
                    Address = rnd.Next(3) == 0 ? null : $"{DemoIndustries.Cities[rnd.Next(DemoIndustries.Cities.Length)]}, {Street()}, кв. {rnd.Next(1, 120)}",
                    Note = rnd.Next(5) == 0 ? "Постоянный клиент" : null
                }, false));
            }
            db.Counterparties.AddRange(customerEntities.Select(c => c.Item1));

            await db.SaveChangesAsync(ct);
            _bankId = bank.Id;
            _suppliers.AddRange(supplierEntities.Select(s => s.Id));
            foreach (var (e, info) in prods) { info.Id = e.Id; _products.Add(info); }
            _customers.AddRange(customerEntities.Select(c => new Party { Id = c.Item1.Id, Wholesale = c.Item2 }));
            _totalWeight = _products.Sum(p => p.Weight);

            // Ожидаемый спрос: ~7,5 чека × ~3 позиции в день, распределённые по популярности
            foreach (var p in _products)
                p.Demand = (decimal)(23 * p.Weight / _totalWeight) * AvgQty(p);
            // Спрос на сырьё — из спроса на продукцию по техкартам
            foreach (var (_, fin, recipe, mats) in recipeDrafts)
                foreach (var (_, mat, q) in mats) mat.Demand += fin.Demand / recipe.Batch * q;
            foreach (var (e, info) in prods) e.MinStock = info.IsService ? 0 : Math.Max(1, Math.Ceiling(info.Daily * 7));
            await db.SaveChangesAsync(ct);

            if (recipeDrafts.Count > 0)
            {
                Report(0.04, "Техкарты");
                var production = new ProductionService(factory, session);
                foreach (var (entity, fin, recipe, mats) in recipeDrafts)
                {
                    var saved = await production.SaveRecipeAsync(new Recipe { Name = recipe.Product, ProductId = entity.Id, OutputQuantity = recipe.Batch },
                        mats.Select(m => new MaterialLine(m.Mat.Id, m.Qty)).ToList(), ct);
                    _recipes.Add(new Batch(fin, saved.Id, recipe.Batch, mats.Select(m => (m.Info, m.Qty)).ToList()));
                }
            }
        }

        // ───────────────────────────── Год работы ─────────────────────────────

        public async Task SimulateAsync()
        {
            var sales = new SalesService(factory, session);
            var purchases = new PurchaseService(factory, session);
            var finance = new FinanceService(factory, session);
            var warehouse = new WarehouseService(factory);
            var start = DateTime.Today.AddDays(-Days);
            var sold = new Dictionary<int, decimal>[28];
            for (int i = 0; i < sold.Length; i++) sold[i] = [];

            // Стартовый капитал и первый завоз всего ассортимента
            var initialStockCost = _products.Where(p => p.Supplier >= 0).Sum(p => InitialQty(p) * p.Cost);
            var capital = RoundTo(initialStockCost * 0.8m, 1000);
            await finance.AddOperationAsync(_bankId, CashDirection.Income, "Начальный остаток", capital,
                "Уставный капитал / собственные средства", start.AddHours(9), ct);
            _bank += capital;
            await finance.AddOperationAsync(_cashId, CashDirection.Income, "Внесение наличных", 5000m, "Размен в кассу", start.AddHours(9.1), ct);
            _cash += 5000m;

            for (int s = 0; s < _suppliers.Count; s++)
            {
                var lines = _products.Where(p => p.Supplier == s).Select(p => (p, InitialQty(p))).ToList();
                if (lines.Count > 0) await PurchaseAsync(purchases, finance, s, lines, start.AddHours(10 + s * 0.3), firstDelivery: true);
            }

            var production = _recipes.Count > 0 ? new ProductionService(factory, session) : null;
            if (production is not null) await ProduceAsync(production, start.AddHours(13), sold[0], 7);

            for (int day = 0; day <= Days; day++)
            {
                ct.ThrowIfCancellationRequested();
                var date = start.AddDays(day);
                Report(0.05 + 0.95 * day / Days, $"Документы: {date:dd.MM.yyyy}");

                // Сдвиг 4-недельного окна продаж
                var bucket = sold[day % 28];
                foreach (var (id, q) in bucket) _products.First(p => p.Id == id).Sold28 -= q;
                bucket.Clear();

                if (day > 0 && date.Day == 1)
                {
                    _lastMonthRevenue = _monthRevenue;
                    _monthRevenue = 0;
                }

                // Отложенные события дня (оплаты долгов, возвраты)
                foreach (var key in _scheduled.Keys.Where(k => k < date.AddDays(1)).ToList())
                {
                    foreach (var action in _scheduled[key]) await action();
                    _scheduled.Remove(key);
                }

                // Закупки: каждый поставщик привозит раз в неделю то, что заканчивается
                for (int s = 0; s < _suppliers.Count; s++)
                    if (day > 0 && (day + s) % 7 == 0)
                    {
                        var lines = _products.Where(p => p.Supplier == s)
                            .Select(p => (p, Need: Reorder(p))).Where(x => x.Need > 0).ToList();
                        if (lines.Count > 0) await PurchaseAsync(purchases, finance, s, lines, date.AddHours(9).AddMinutes(rnd.Next(0, 90)), false);
                    }

                // Цех: утренний выпуск того, что заканчивается
                if (production is not null && day > 0) await ProduceAsync(production, date.AddHours(6), bucket, 5);

                // Продажи: 5–10 чеков в день
                if (day > 0)
                {
                    var count = rnd.Next(5, 11);
                    var times = Enumerable.Range(0, count).Select(_ => date.AddHours(9).AddMinutes(rnd.Next(0, 11 * 60))).OrderBy(t => t).ToList();
                    foreach (var time in times)
                        await SaleAsync(sales, time, bucket);
                }

                await ExpensesAsync(finance, date, day);

                // Списания (порча, срок годности) — раз в месяц
                if (profile.HasSpoilage && date.Day == 25)
                {
                    var lines = _products.Where(p => p.Stock > 2).OrderBy(_ => rnd.Next()).Take(rnd.Next(2, 6))
                        .Select(p => new TransferLine(p.Id, Qty(p, Math.Max(1, p.Stock * 0.05m)))).Where(l => l.Quantity > 0).ToList();
                    if (lines.Count > 0)
                    {
                        await warehouse.WriteOffAsync(_warehouseId, lines, profile.Industry == DemoIndustry.Pharmacy ? "Истёк срок годности" : "Порча товара",
                            "Акт списания", date.AddHours(20), session.UserId, ct);
                        foreach (var l in lines) _products.First(p => p.Id == l.ProductId).Stock -= l.Quantity;
                    }
                }
            }
        }

        /// <summary>Заказ поставщику: если запаса меньше чем на ~10 дней — довозим до 4 недель.</summary>
        private static decimal Reorder(Prod p)
        {
            if (p.Stock > p.Daily * 10 + p.MinLot / 2) return 0;
            return Qty(p, p.Daily * 28 + p.MinLot - p.Stock);
        }

        private static decimal InitialQty(Prod p) => Qty(p, p.Daily * 30 + p.MinLot);

        private async Task PurchaseAsync(PurchaseService purchases, FinanceService finance, int supplierIdx,
            List<(Prod P, decimal Qty)> lines, DateTime date, bool firstDelivery)
        {
            var doc = lines.Where(l => l.Qty > 0).Select(l => new DocumentLine(l.P.Id, l.Qty, l.P.Cost)).ToList();
            if (doc.Count == 0) return;
            var supplierId = _suppliers[supplierIdx];
            var purchase = await purchases.CreatePurchaseAsync(new PurchaseRequest(supplierId, _warehouseId, doc,
                Comment: firstDelivery ? "Первичный завоз товара" : $"Накладная поставщика № {rnd.Next(1000, 99999)}", Date: date,
                UpdateProductPurchasePrice: false), ct);
            foreach (var (p, q) in lines) p.Stock += q;
            _supplierDebt[supplierId] = _supplierDebt.GetValueOrDefault(supplierId) + purchase.Total;

            // Оплата: часть сразу, остаток — с отсрочкой 7–21 день
            var now = rnd.Next(10) < (firstDelivery ? 6 : 5) ? purchase.Total : Money.Round(purchase.Total * (decimal)(rnd.NextDouble() * 0.5));
            if (now > 0) await PaySupplierAsync(finance, supplierId, now, date.AddMinutes(30));
            if (purchase.Total - now > 0)
                Schedule(date.AddDays(rnd.Next(7, 22)).Date.AddHours(11), () => PaySupplierAsync(finance, supplierId, null, null));
        }

        /// <summary>Оплата поставщику с расчётного счёта (или из кассы); amount null — весь текущий долг.</summary>
        private async Task PaySupplierAsync(FinanceService finance, int supplierId, decimal? amount, DateTime? date)
        {
            var debt = _supplierDebt.GetValueOrDefault(supplierId);
            var sum = Math.Min(amount ?? debt, debt);
            if (sum <= 0) return;
            var when = date ?? _scheduledNow;
            var (cashId, available) = _bank >= sum ? (_bankId, _bank) : _cash >= sum ? (_cashId, _cash) : _bank >= _cash ? (_bankId, _bank) : (_cashId, _cash);
            var pay = Math.Min(sum, RoundTo(available - 500, 100));
            if (pay < 100)
            {
                // Денег нет — переносим оплату на неделю
                Schedule(when.Date.AddDays(7).AddHours(11), () => PaySupplierAsync(finance, supplierId, amount, null));
                return;
            }
            await finance.CounterpartyPaymentAsync(new CounterpartyPaymentRequest(CashDirection.Expense, cashId, supplierId, pay,
                Comment: cashId == _bankId ? $"Платёжное поручение № {rnd.Next(100, 9999)}" : "Оплата наличными", Date: when), ct);
            if (cashId == _bankId) _bank -= pay; else _cash -= pay;
            _supplierDebt[supplierId] = debt - pay;
            if (pay < sum) Schedule(when.Date.AddDays(5).AddHours(11), () => PaySupplierAsync(finance, supplierId, sum - pay, null));
        }

        private async Task SaleAsync(SalesService sales, DateTime time, Dictionary<int, decimal> bucket)
        {
            // ~12% чеков — оптовые клиенты, ещё ~15% — постоянные покупатели по карте клиента
            var roll = rnd.Next(100);
            var wholesalers = _customers.Where(c => c.Wholesale).ToList();
            Party? customer = roll < 12 && wholesalers.Count > 0 ? wholesalers[rnd.Next(wholesalers.Count)]
                : roll < 27 ? _customers[rnd.Next(_customers.Count)] : null;
            var wholesale = customer?.Wholesale == true;

            var lineCount = wholesale ? rnd.Next(3, 11) : rnd.Next(1, 100) < 45 ? 1 : rnd.Next(2, 7);
            var picked = new Dictionary<Prod, decimal>();
            for (int i = 0; i < lineCount * 3 && picked.Count < lineCount; i++)
            {
                var p = PickProduct();
                if (picked.ContainsKey(p) || p.Stock <= 0) continue;
                var q = Math.Min(p.Stock, SaleQty(p, wholesale));
                if (p.Unit != "кг") q = Math.Floor(q);
                if (q > 0) picked[p] = q;
            }
            if (picked.Count == 0) return;

            var lines = picked.Select(x => new DocumentLine(x.Key.Id,
                x.Value, wholesale ? RoundPrice(x.Key.Price * 0.95m) : x.Key.Price)).ToList();
            var total = Money.Round(lines.Sum(l => Money.Round(l.Quantity * l.Price)));

            // Оплата: наличные / карта / смешанная / в долг (оптовики)
            List<SalePayment>? payments = null;
            var onCredit = false;
            decimal toCash = 0, toBank = 0;
            if (wholesale && rnd.Next(100) < 55)
            {
                onCredit = true;
                var prepay = rnd.Next(3) == 0 ? Money.Round(total * 0.3m) : 0;
                payments = prepay > 0 ? [new SalePayment(_bankId, prepay)] : [];
                toBank = prepay;
            }
            else
            {
                var kind = rnd.Next(100);
                if (kind < 60) toCash = total;
                else if (kind < 92 || total < 20) toBank = total;
                else { toCash = RoundTo(total / 2, 10); toBank = total - toCash; }
                payments = [.. new[] { new SalePayment(_cashId, toCash), new SalePayment(_bankId, toBank) }.Where(p => p.Amount > 0)];
            }

            var sale = await sales.CreateSaleAsync(new SaleRequest(_warehouseId, toBank > 0 && toCash == 0 ? _bankId : _cashId, lines,
                customer?.Id, wholesale ? $"Счёт-фактура № {rnd.Next(100, 9999)}" : null, time, payments, onCredit), ct);
            _cash += toCash;
            _bank += toBank;
            _monthRevenue += sale.Total;
            foreach (var (p, q) in picked)
            {
                p.Stock -= q;
                p.Sold28 += q;
                bucket[p.Id] = bucket.GetValueOrDefault(p.Id) + q;
            }

            if (onCredit && customer is not null)
            {
                var debt = sale.Total - sale.PaidAmount;
                customer.Debt += debt;
                // Оптовик гасит долг через 5–30 дней, иногда частями
                Schedule(time.Date.AddDays(rnd.Next(5, 31)).AddHours(12), () => CustomerPaysAsync(customer, debt));
            }
            else if (rnd.Next(100) < 2 && time.Date < DateTime.Today)
            {
                // Редкий возврат: покупатель приносит одну позицию в течение 1–3 дней
                var item = sale.Items.OrderBy(_ => rnd.Next()).First();
                var back = item.Quantity >= 2 ? Math.Floor(item.Quantity / 2) : item.Quantity;
                var when = time.AddDays(rnd.Next(1, 4));
                if (when.Date <= DateTime.Today && toCash > 0)
                    Schedule(when, async () =>
                    {
                        var amount = Money.Round(back * item.Price);
                        if (_cash < amount) return;
                        await sales.CreateReturnAsync(new SaleReturnRequest(sale.Id, _cashId, [new ReturnLine(item.Id, back)],
                            rnd.Next(2) == 0 ? "Не подошёл товар" : "Брак / заводской дефект", when), ct);
                        _cash -= amount;
                        var p = _products.First(x => x.Id == item.ProductId);
                        p.Stock += back;
                    });
            }
        }

        private FinanceService? _finance;
        private DateTime _scheduledNow;

        private async Task CustomerPaysAsync(Party customer, decimal amount)
        {
            _finance ??= new FinanceService(factory, session);
            var pay = Math.Min(amount, customer.Debt);
            if (pay <= 0) return;
            var partial = rnd.Next(4) == 0 && pay > 500;
            if (partial) pay = RoundTo(pay / 2, 10);
            var toBank = rnd.Next(3) > 0;
            await _finance.CounterpartyPaymentAsync(new CounterpartyPaymentRequest(CashDirection.Income, toBank ? _bankId : _cashId,
                customer.Id, pay, Article: "Оплата от клиента", Comment: toBank ? "Перечисление на расчётный счёт" : "Погашение долга наличными",
                Date: _scheduledNow), ct);
            if (toBank) _bank += pay; else _cash += pay;
            customer.Debt -= pay;
            if (partial) Schedule(_scheduledNow.Date.AddDays(rnd.Next(7, 15)).AddHours(12), () => CustomerPaysAsync(customer, amount - pay));
        }

        private async Task ExpensesAsync(FinanceService finance, DateTime date, int day)
        {
            if (day == 0) return;
            // Расходы — от оборота прошлого месяца (в первый месяц — по фактическим дням)
            var revenue = _lastMonthRevenue > 0 ? _lastMonthRevenue : day >= 7 ? _monthRevenue / day * 30 : 0;

            async Task Pay(string article, decimal amount, string comment, int hour, bool fromBank)
            {
                amount = RoundTo(amount, 10);
                var balance = fromBank ? _bank : _cash;
                if (amount <= 0) return;
                if (balance - amount < 200)
                {
                    fromBank = !fromBank;
                    balance = fromBank ? _bank : _cash;
                    if (balance - amount < 200) return;
                }
                await finance.AddOperationAsync(fromBank ? _bankId : _cashId, CashDirection.Expense, article, amount, comment,
                    date.AddHours(hour).AddMinutes(rnd.Next(0, 50)), ct);
                if (fromBank) _bank -= amount; else _cash -= amount;
            }

            var month = L(date);
            // Расходы ≈ 10–12% оборота: компания работает с прибылью
            if (date.Day == 1) await Pay("Аренда", revenue * 0.025m, $"Аренда помещения за {month}", 10, true);
            if (date.Day == 5) await Pay("Зарплата", revenue * 0.035m, $"Зарплата сотрудникам за {L(date.AddMonths(-1))}", 15, false);
            if (date.Day == 20) await Pay("Зарплата", revenue * 0.015m, $"Аванс за {month}", 15, false);
            if (date.Day == 10) await Pay("Коммунальные услуги", revenue * 0.005m + 150, $"Электроэнергия, вода, вывоз мусора за {month}", 11, true);
            if (date.Day == 15 && date.Month % 3 == 1) await Pay("Налоги", revenue * 0.06m, "Налог с оборота за квартал", 11, true);
            if (date.Day == 12) await Pay("Реклама", revenue * 0.004m + 100, rnd.Next(2) == 0 ? "Реклама в Instagram" : "Баннер, листовки", 12, true);
            if (date.DayOfWeek == DayOfWeek.Monday) await Pay("Транспорт", 80 + rnd.Next(0, 200), "Доставка товара, топливо", 9, false);
            if (rnd.Next(100) < 6) await Pay("Прочий расход", 20 + rnd.Next(0, 150),
                new[] { "Канцтовары", "Хозяйственные нужды", "Чековая лента", "Ремонт оборудования", "Пакеты" }[rnd.Next(5)], 13, false);

            // Конец месяца: владелец забирает часть выручки из кассы
            if (date.AddDays(1).Day == 1 && _cash > 10_000)
                await Pay("Изъятие наличных", (_cash - 5000) * 0.25m, "Изъятие выручки владельцем", 21, false);

            // Инкассация: наличные свыше лимита — на расчётный счёт (через приход/расход, чтобы сохранить дату)
            if (date.DayOfWeek == DayOfWeek.Saturday && _cash > 30_000)
            {
                var sum = RoundTo(_cash - 15_000, 1000);
                var at = date.AddHours(20);
                await finance.AddOperationAsync(_cashId, CashDirection.Expense, "Изъятие наличных", sum, "Сдача выручки в банк (инкассация)", at, ct);
                await finance.AddOperationAsync(_bankId, CashDirection.Income, "Внесение наличных", sum, "Сдача выручки в банк (инкассация)", at.AddMinutes(30), ct);
                _cash -= sum;
                _bank += sum;
            }
        }

        /// <summary>Выпуск продукции: если запаса меньше чем на 3 дня — производим до <paramref name="days"/> дней (сколько хватает сырья).</summary>
        private async Task ProduceAsync(ProductionService production, DateTime date, Dictionary<int, decimal> bucket, int days)
        {
            foreach (var b in _recipes)
            {
                var fin = b.Product;
                if (fin.Stock > fin.Daily * 3) continue;
                var batches = Math.Ceiling(Math.Max(0, fin.Daily * days - fin.Stock) / b.Size);
                foreach (var (mat, q) in b.Materials) batches = Math.Min(batches, Math.Floor(mat.Stock / q));
                if (batches <= 0) continue;
                var qty = batches * b.Size;
                var materials = b.Materials.Select(m => new MaterialLine(m.Mat.Id, m.Qty * batches)).ToList();
                await production.ProduceAsync(new ProductionRequest(_warehouseId, fin.Id, qty, materials,
                    ExtraCost: RoundTo(qty * fin.Cost * 0.12m, 1), Comment: "Выпуск по техкарте", Date: date.AddMinutes(rnd.Next(0, 60)), RecipeId: b.RecipeId), ct);
                fin.Stock += qty;
                foreach (var (mat, q) in b.Materials)
                {
                    var used = q * batches;
                    mat.Stock -= used;
                    mat.Sold28 += used;   // расход сырья — основа для заказа поставщику
                    bucket[mat.Id] = bucket.GetValueOrDefault(mat.Id) + used;
                }
            }
        }

        private sealed record Batch(Prod Product, int RecipeId, decimal Size, List<(Prod Mat, decimal Qty)> Materials);

        // ───────────────────────────── Вспомогательное ─────────────────────────────

        private void Schedule(DateTime when, Func<Task> action)
        {
            if (when.Date > DateTime.Today) return;
            if (!_scheduled.TryGetValue(when, out var list)) _scheduled[when] = list = [];
            list.Add(async () => { _scheduledNow = when; await action(); });
        }

        private Prod PickProduct()
        {
            var x = rnd.NextDouble() * _totalWeight;
            foreach (var p in _products)
                if ((x -= p.Weight) <= 0) return p;
            return _products[^1];
        }

        private decimal SaleQty(Prod p, bool wholesale)
        {
            var k = wholesale ? rnd.Next(4, 16) : 1;
            return p.Unit switch
            {
                "усл" => wholesale ? rnd.Next(1, 4) : 1,
                "кг" => Math.Round((decimal)(0.3 + rnd.NextDouble() * 2.7) * k, 1),
                "м" => rnd.Next(2, 26) * k,
                "м²" => rnd.Next(4, 31) * (wholesale ? 3 : 1),
                "мешок" => rnd.Next(1, 6) * (wholesale ? rnd.Next(3, 8) : 1),
                _ when p.Price > 2000 => wholesale ? rnd.Next(1, 4) : 1,
                _ when p.Price > 300 => wholesale ? rnd.Next(2, 6) : 1,
                _ => (rnd.Next(10) < 7 ? 1 : rnd.Next(2, 5)) * k
            };
        }

        private static decimal MinLot(Prod p) => p.Unit switch
        {
            "кг" => 5,
            "м" or "м²" => 50,
            "мешок" => 10,
            _ => p.Cost switch { < 50 => 6, < 500 => 3, _ => 1 }
        };

        /// <summary>Среднее количество в одной строке чека (с учётом оптовых покупателей).</summary>
        private static decimal AvgQty(Prod p) => p.Unit switch
        {
            "кг" => 3, "м" => 25, "м²" => 20, "мешок" => 5,
            _ => p.Price > 2000 ? 1.1m : p.Price > 300 ? 1.4m : 3
        };

        private static decimal Qty(Prod p, decimal q) => Math.Max(0, Math.Ceiling(q));

        private static string L(DateTime d) =>
            d.ToString("MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

        private void Report(double f, string step) => progress?.Report(new DemoProgress(f, step));

        private string Phone(bool landline) => landline && rnd.Next(2) == 0
            ? $"+992 44 6{rnd.Next(10, 99)} {rnd.Next(10, 99)} {rnd.Next(10, 99)}"
            : $"+992 {DemoIndustries.PhonePrefixes[rnd.Next(DemoIndustries.PhonePrefixes.Length)]} {rnd.Next(100, 999)} {rnd.Next(10, 99)} {rnd.Next(10, 99)}";

        private string Street() => $"{DemoIndustries.Streets[rnd.Next(DemoIndustries.Streets.Length)]}, {rnd.Next(1, 150)}";

        private static T Add<T>(TradeDbContext db, T entity) where T : class { db.Add(entity); return entity; }

        private static decimal Step(decimal price) => price switch { < 10 => 0.5m, < 100 => 1, < 1000 => 5, _ => 10 };
        private static decimal RoundPrice(decimal price) => Math.Max(Step(price), RoundTo(price, Step(price)));
        private static decimal RoundTo(decimal v, decimal step) => Math.Round(v / step, MidpointRounding.AwayFromZero) * step;

        private static string Abbrev(string category) =>
            string.Concat(category.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).Take(2)
                .Select(w => char.ToUpperInvariant(w[0]).ToString() + (w.Length > 1 ? char.ToUpperInvariant(w[1]).ToString() : "")));

        private static string Ean13(string twelve)
        {
            var sum = twelve.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
            return twelve + (10 - sum % 10) % 10;
        }

        /// <summary>«Сок {Rich|J7*0.8} 1 л» → («Сок Rich 1 л», 1), («Сок J7 1 л», 0.8).</summary>
        private static IEnumerable<(string Name, decimal Factor)> Expand(string template)
        {
            var m = Regex.Match(template, @"\{([^{}]*)\}");
            if (!m.Success)
            {
                yield return (Regex.Replace(template, @"\s{2,}", " ").Trim(), 1m);
                yield break;
            }
            foreach (var option in m.Groups[1].Value.Split('|'))
            {
                var parts = option.Split('*');
                var factor = parts.Length > 1 ? decimal.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 1m;
                var rest = template[..m.Index] + parts[0] + template[(m.Index + m.Length)..];
                foreach (var (name, f) in Expand(rest)) yield return (name, f * factor);
            }
        }
    }
}
