using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Товар на витрине кассы: товар, его остаток на выбранном складе и сколько уже в чеке.</summary>
public partial class PosItem(Product product) : ObservableObject
{
    public Product Product { get; } = product;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOut), nameof(StockText))] private decimal _stock;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(InCart))] private decimal _inCartQuantity;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Price))] private bool _isWholesale;

    /// <summary>Цена на витрине — розничная или оптовая, по переключателю в чеке.</summary>
    public decimal Price => SalesViewModel.PriceOf(Product, IsWholesale);

    public bool IsOut => !Product.IsService && Stock <= 0;
    public bool InCart => InCartQuantity > 0;
    public string StockText => Product.IsService ? L.T("услуга") : L.F("{0:0.###} {1}", Stock, Product.Unit);
}

/// <summary>Размер карточек витрины: подпись и масштаб.</summary>
public sealed record PosTileSize(string Name, double Scale);

/// <summary>Категория на витрине кассы (null-категория — «Все товары»).</summary>
public sealed record PosCategory(Category? Category, string Name, int Count)
{
    public byte[]? Image => Category?.Image;
    public bool IsAll => Category is null;
}

/// <summary>
/// Быстрая касса (вкладка «Новая продажа»): слева витрина — поиск, категории с картинками и товары
/// (плитки или таблица), справа чек и оплата. Клавиатура: F2 — поиск, ↑/↓ — выбор товара, Enter — в чек,
/// Esc — очистить поиск, F9 — провести.
/// </summary>
public partial class SalesViewModel
{
    private readonly List<PosItem> _allItems = new();
    private Dictionary<int, decimal> _stock = new();

    public ObservableCollection<PosCategory> PosCategories { get; } = new();
    /// <summary>Постраничный вывод витрины: в интерфейсе только текущая страница — быстро даже на 10 000+ товаров.</summary>
    public ReportPager<PosItem> ShowcasePager { get; } = new(_ => true) { PageSize = 50 };
    /// <summary>Товары текущей страницы витрины — с учётом категории и поиска.</summary>
    public ObservableCollection<PosItem> Showcase => ShowcasePager.Items;

    [ObservableProperty] private PosCategory? _selectedPosCategory;
    [ObservableProperty] private PosItem? _selectedItem;
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private bool _showTable = App.Ui.PosShowTable;
    [ObservableProperty] private bool _onlyInStock = App.Ui.PosOnlyInStock;
    /// <summary>Показывать ленту категорий над витриной.</summary>
    [ObservableProperty] private bool _showCategories = App.Ui.PosShowCategories;

    // Скрыли категории — сбрасываем фильтр на «Все товары», иначе витрина останется отфильтрованной
    partial void OnShowCategoriesChanged(bool value)
    {
        if (!value && PosCategories.Count > 0) SelectedPosCategory = PosCategories[0];
        App.Ui.PosShowCategories = value;
        App.Ui.Save();
    }

    /// <summary>Показывать фото на карточках (без фото карточки компактнее).</summary>
    [ObservableProperty] private bool _showTilePhotos = App.Ui.PosShowTilePhotos;

    partial void OnShowTilePhotosChanged(bool value)
    {
        App.Ui.PosShowTilePhotos = value;
        App.Ui.Save();
    }

    /// <summary>Печать чека после проведения продажи (флажок в окне оформления, запоминается).</summary>
    [ObservableProperty] private bool _printReceiptAfterSale = App.Ui.PosPrintReceipt;
    partial void OnPrintReceiptAfterSaleChanged(bool value) { App.Ui.PosPrintReceipt = value; App.Ui.Save(); }

    /// <summary>Печать накладной после проведения продажи.</summary>
    [ObservableProperty] private bool _printInvoiceAfterSale = App.Ui.PosPrintInvoice;
    partial void OnPrintInvoiceAfterSaleChanged(bool value) { App.Ui.PosPrintInvoice = value; App.Ui.Save(); }

    /// <summary>Варианты размера карточек на витрине.</summary>
    public IReadOnlyList<PosTileSize> TileSizes { get; } =
    [
        new(L.T("Маленький"), 0.85),
        new(L.T("Обычный"), 1.0),
        new(L.T("Большой"), 1.25),
        new(L.T("Очень большой"), 1.5),
    ];

    [ObservableProperty] private double _tileScale = App.Ui.PosTileScale is > 0 ? App.Ui.PosTileScale : 1;

    partial void OnTileScaleChanged(double value)
    {
        App.Ui.PosTileScale = value;
        App.Ui.Save();
    }

    partial void OnShowTableChanged(bool value)
    {
        App.Ui.PosShowTable = value;
        App.Ui.Save();
    }
    [ObservableProperty] private bool _isShowcaseEmpty;

    /// <summary>Окно ставит фокус в строку поиска (после проведения, по F2).</summary>
    public event EventHandler? FocusSearchRequested;

    /// <summary>Заполняет витрину после загрузки товаров (вызывается из LoadAsync).</summary>
    private async Task LoadShowcaseAsync()
    {
        var thumbs = await catalog.GetProductThumbnailsAsync();
        foreach (var p in Products) p.Thumbnail = thumbs.GetValueOrDefault(p.Id);
        var categories = await catalog.GetAllAsync<Category>(activeOnly: true);

        _allItems.Clear();
        _allItems.AddRange(Products.Select(p => new PosItem(p) { IsWholesale = IsWholesale }));
        await LoadStockAsync();

        var selectedId = SelectedPosCategory is { } sel ? sel.Category?.Id : App.Ui.PosCategoryId;
        PosCategories.Clear();
        PosCategories.Add(new PosCategory(null, L.T("Все товары"), _allItems.Count));
        foreach (var c in categories)
        {
            var count = _allItems.Count(i => i.Product.CategoryId == c.Id);
            if (count > 0) PosCategories.Add(new PosCategory(c, c.Name, count));
        }
        SelectedPosCategory = PosCategories.FirstOrDefault(c => c.Category?.Id == selectedId) ?? PosCategories[0];
        RefreshShowcase();
    }

    /// <summary>Остатки по выбранному складу.</summary>
    private async Task LoadStockAsync()
    {
        if (Warehouse is null) { _stock = new(); }
        else _stock = (await warehouses.GetBalancesAsync(Warehouse.Id, includeZero: true))
            .GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));
        foreach (var i in _allItems) i.Stock = _stock.GetValueOrDefault(i.Product.Id);
        SyncCartMarks();
    }

    partial void OnWarehouseChanged(Warehouse? value)
    {
        if (value is not null && App.Ui.PosWarehouseId != value.Id)
        {
            App.Ui.PosWarehouseId = value.Id;
            App.Ui.Save();
        }
        if (_allItems.Count > 0) _ = RunAsync(async () => { await LoadStockAsync(); RefreshShowcase(); });
    }

    partial void OnSearchTextChanged(string? value) => RefreshShowcase();
    partial void OnSelectedPosCategoryChanged(PosCategory? value)
    {
        RefreshShowcase();
        if (value is null || App.Ui.PosCategoryId == value.Category?.Id) return;
        App.Ui.PosCategoryId = value.Category?.Id;
        App.Ui.Save();
    }

    partial void OnOnlyInStockChanged(bool value)
    {
        RefreshShowcase();
        App.Ui.PosOnlyInStock = value;
        App.Ui.Save();
    }

    [RelayCommand]
    private void ToggleView() => ShowTable = !ShowTable;

    private void RefreshShowcase()
    {
        var term = SearchText?.Trim();
        IEnumerable<PosItem> q = _allItems;
        // При поиске ищем по всем категориям — так быстрее найти товар
        if (string.IsNullOrEmpty(term) && SelectedPosCategory?.Category is { } c) q = q.Where(i => i.Product.CategoryId == c.Id);
        if (OnlyInStock) q = q.Where(i => !i.IsOut);

        List<PosItem> list;
        if (string.IsNullOrEmpty(term))
            list = q.OrderBy(i => i.Product.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        else
            // Сначала точный код, затем начало названия, затем вхождение
            list = q.Select(i => (i, rank: Rank(i.Product, term))).Where(x => x.rank >= 0)
                .OrderBy(x => x.rank).ThenBy(x => x.i.Product.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.i).ToList();

        ShowcasePager.SetSource(list);
        IsShowcaseEmpty = list.Count == 0;
        SelectedItem = string.IsNullOrEmpty(term) ? null : Showcase.FirstOrDefault();
    }


    private static int Rank(Product p, string term) =>
        string.Equals(p.Barcode, term, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Sku, term, StringComparison.OrdinalIgnoreCase) ? 0
        : p.Name.StartsWith(term, StringComparison.CurrentCultureIgnoreCase) ? 1
        : p.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
          || (p.Sku?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
          || (p.Barcode?.StartsWith(term, StringComparison.OrdinalIgnoreCase) ?? false) ? 2 : -1;

    /// <summary>Enter в строке поиска: точный штрихкод/артикул → в чек; иначе выбранный товар; неизвестный код — создать товар.</summary>
    [RelayCommand]
    private async Task SearchEnterAsync()
    {
        var term = SearchText?.Trim();
        if (string.IsNullOrEmpty(term))
        {
            if (SelectedItem is { } sel) AddToCart(sel.Product);
            return;
        }
        var exact = Products.FirstOrDefault(p => string.Equals(p.Barcode, term, StringComparison.OrdinalIgnoreCase))
                    ?? Products.FirstOrDefault(p => string.Equals(p.Sku, term, StringComparison.OrdinalIgnoreCase));
        if (exact is null && TryParseScaleBarcode(term, out var code, out var weight))
        {
            var weighted = Products.FirstOrDefault(p => p.IsWeighted && SameScaleCode(p.Barcode, code));
            if (weighted is null) { Dialogs.Error(L.F("Весовой товар с кодом {0} не найден. Укажите этот код в штрихкоде товара и отметьте «Весовой товар».", code)); return; }
            if (weight <= 0) { Dialogs.Error(L.T("В штрихкоде весов нулевой вес.")); return; }
            AddToCart(weighted, weight);
            return;
        }
        var product = exact ?? SelectedItem?.Product ?? Showcase.FirstOrDefault()?.Product;
        if (product is null)
        {
            if (!Dialogs.Confirm(L.F("Товар «{0}» не найден.\nСоздать новый товар?", term))) return;
            var looksLikeCode = term.All(char.IsLetterOrDigit) && term.Any(char.IsDigit);
            var created = await CatalogDialogs.EditAsync(Products, looksLikeCode ? new Product { Barcode = term } : new Product { Name = term });
            if (created?.Saved is not { } p) return;
            _allItems.Add(new PosItem(p) { IsWholesale = IsWholesale, Stock = _stock.GetValueOrDefault(p.Id) });
            product = p;
        }
        AddToCart(product);
    }

    /// <summary>Клик по плитке или строке таблицы.</summary>
    [RelayCommand]
    private void Pick(PosItem? item)
    {
        if (item is not null) AddToCart(item.Product);
    }

    /// <summary>Стрелки в строке поиска двигают выделение на витрине.</summary>
    public void MoveSelection(int delta)
    {
        if (Showcase.Count == 0) return;
        var i = SelectedItem is null ? -1 : Showcase.IndexOf(SelectedItem);
        SelectedItem = Showcase[Math.Clamp(i + delta, 0, Showcase.Count - 1)];
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    // ── Весы: EAN-13 вида ПП ККККК ВВВВВ К (префикс весов, код товара, вес в граммах, контрольная цифра) ──

    /// <summary>Префикс весового штрихкода из настроек (null — весы не используются).</summary>
    public string? ScalePrefix { get; set; }

    private bool TryParseScaleBarcode(string term, out string code, out decimal weightKg)
    {
        code = "";
        weightKg = 0;
        if (ScalePrefix is not { Length: 2 } prefix || term.Length != 13 || !term.All(char.IsAsciiDigit) || !term.StartsWith(prefix))
            return false;
        if (!IsValidEan13(term)) return false;
        code = term.Substring(2, 5);
        weightKg = int.Parse(term.Substring(7, 5)) / 1000m;
        return true;
    }

    private static bool IsValidEan13(string s)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (s[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10 == s[12] - '0';
    }

    /// <summary>Код товара на весах сравнивается как число: «00123» = «123».</summary>
    private static bool SameScaleCode(string? barcode, string code)
    {
        var b = barcode?.Trim();
        if (string.IsNullOrEmpty(b) || !b.All(char.IsAsciiDigit) || b.Length > 5) return false;
        return int.Parse(b) == int.Parse(code);
    }

    private void AddToCart(Product product, decimal quantity = 1)
    {
        AddOrMerge(product, quantity, DefaultPrice(product));
        SelectedLine = Lines.LastOrDefault(l => l.Product.Id == product.Id);
        // Сканер/поиск — очищаем строку; клик по плитке при пустом поиске фильтр не трогает
        if (!string.IsNullOrEmpty(SearchText)) SearchText = "";
        LastResult = null;
        RaiseTotals();
        FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Отметки «уже в чеке: N» на плитках.</summary>
    private void SyncCartMarks()
    {
        var inCart = Lines.GroupBy(l => l.Product.Id).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        foreach (var i in _allItems) i.InCartQuantity = inCart.GetValueOrDefault(i.Product.Id);
    }

    // ── Корзина ──
    [ObservableProperty] private DocumentLineViewModel? _selectedLine;

    [RelayCommand]
    private void IncreaseLine(DocumentLineViewModel? line)
    {
        if (line is not null) line.Quantity += 1;
    }

    [RelayCommand]
    private void DecreaseLine(DocumentLineViewModel? line)
    {
        if (line is null) return;
        if (line.Quantity > 1) line.Quantity -= 1;
        else RemoveFromCart(line);
    }

    [RelayCommand]
    private void RemoveFromCart(DocumentLineViewModel? line)
    {
        RemoveLineCommand.Execute(line);
        RaiseTotals();
    }

    [RelayCommand]
    private void NewSale()
    {
        ClearLines();
        ResetDiscount();
        ClearPayments();
        Comment = null;
        Customer = null;
        CurrentDraftId = null;
        EditingSale = null;
        LastResult = null;
        RaiseTotals();
        FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    // ── Итоги: скидка (% или сумма — поля связаны), оплаты, остаток, сдача ──
    [ObservableProperty] private decimal _discountPercent;
    [ObservableProperty] private decimal _discountSum;
    /// <summary>Скидку последней вводили суммой — она держится при изменении чека, процент пересчитывается.</summary>
    private bool _discountBySum;
    private bool _syncingDiscount;

    public int ItemsCount => Lines.Count;
    public bool IsCartEmpty => Lines.Count == 0;
    public decimal ItemsQuantity => Lines.Sum(l => l.Quantity);

    /// <summary>Доля к оплате после скидки (1 — без скидки).</summary>
    private decimal DiscountFactor => _discountBySum
        ? (Total > 0 ? (Total - Math.Clamp(DiscountSum, 0, Total)) / Total : 1)
        : (100 - Math.Clamp(DiscountPercent, 0, 100)) / 100;
    /// <summary>Цена строки со скидкой (скидка применяется к каждой цене — так она попадает в документ).</summary>
    private decimal Discounted(decimal price) => Money.Round(price * DiscountFactor);
    public decimal ToPay => PricedLines().Sum(x => Money.Round(x.Quantity * x.Price));

    /// <summary>
    /// Строки с ценой после скидки. Скидка суммой должна сойтись до копейки: округление цен каждой строки
    /// даёт расхождение (6 → 5,99), поэтому остаток округления добавляем к одной строке
    /// (строку с количеством > 1 при необходимости делим: N−1 шт. и 1 шт.).
    /// </summary>
    private List<(long ProductId, decimal Quantity, decimal Price)> PricedLines()
    {
        var list = Lines.Select(l => ((long)l.Product.Id, l.Quantity, Discounted(l.Price))).ToList();
        if (!_discountBySum || list.Count == 0 || Total <= 0) return list;
        var target = Total - Money.Round(Math.Clamp(DiscountSum, 0, Total));
        var diff = target - list.Sum(x => Money.Round(x.Item2 * x.Item3));
        if (diff == 0) return list;
        var i = list.FindIndex(x => x.Item2 == 1 && x.Item3 + diff >= 0);
        if (i >= 0) { list[i] = (list[i].Item1, 1, list[i].Item3 + diff); return list; }
        i = list.FindIndex(x => x.Item2 > 1 && x.Item2 == decimal.Truncate(x.Item2) && x.Item3 + diff >= 0);
        if (i >= 0)
        {
            var x = list[i];
            list[i] = (x.Item1, x.Item2 - 1, x.Item3);
            list.Insert(i + 1, (x.Item1, 1, x.Item3 + diff));
        }
        return list;
    }
    public decimal DiscountAmount => Total - ToPay;
    public bool HasDiscount => DiscountAmount > 0;

    partial void OnDiscountPercentChanged(decimal value)
    {
        if (!_syncingDiscount)
        {
            _discountBySum = false;
            SyncDiscount();
        }
        RaiseTotals();
    }

    partial void OnDiscountSumChanged(decimal value)
    {
        if (!_syncingDiscount)
        {
            _discountBySum = true;
            SyncDiscount();
        }
        RaiseTotals();
    }

    /// <summary>Пересчитать второе поле скидки из того, которое вводили.</summary>
    private void SyncDiscount()
    {
        _syncingDiscount = true;
        if (_discountBySum)
            DiscountPercent = Total > 0 ? Math.Round(Math.Clamp(DiscountSum, 0, Total) / Total * 100, 2) : 0;
        else
            DiscountSum = Money.Round(Total * Math.Clamp(DiscountPercent, 0, 100) / 100);
        _syncingDiscount = false;
    }

    private void ResetDiscount()
    {
        _syncingDiscount = true;
        DiscountPercent = 0;
        DiscountSum = 0;
        _discountBySum = false;
        _syncingDiscount = false;
    }

    partial void OnCustomerChanged(Counterparty? value) => RaiseTotals();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Total))
        {
            SyncDiscount();
            RaiseTotals();
        }
    }

    private void RaiseTotals()
    {
        SyncAutoPayment();
        foreach (var n in new[] { nameof(ItemsCount), nameof(IsCartEmpty), nameof(ItemsQuantity), nameof(ToPay), nameof(DiscountAmount), nameof(HasDiscount),
                     nameof(PaidSum), nameof(Remainder), nameof(Change), nameof(HasChange), nameof(Overpay), nameof(HasOverpay), nameof(DebtAmount), nameof(HasDebt), nameof(CreditNeedsCustomer) })
            OnPropertyChanged(n);
        SyncCartMarks();
    }

    /// <summary>Итог последней продажи — зелёная плашка над чеком вместо окна-сообщения.</summary>
    [ObservableProperty] private string? _lastResult;

    private IReadOnlyList<Core.Services.DocumentLine> CartLines() =>
        PricedLines().Select(x => new Core.Services.DocumentLine((int)x.ProductId, x.Quantity, x.Price)).ToList();
}

/// <summary>Строка оплаты: касса и сумма.</summary>
public partial class PaymentPart : ObservableObject
{
    [ObservableProperty] private CashRegister? _cashRegister;
    [ObservableProperty] private decimal _amount;
}

public partial class SalesViewModel
{
    /// <summary>
    /// Оплаты по кассам. Сумма оплат меньше «К оплате» — остаток записывается в долг покупателя
    /// (нужен покупатель); больше — разница выдаётся сдачей.
    /// </summary>
    public ObservableCollection<PaymentPart> Payments { get; } = new();
    /// <summary>Пока кассир не менял оплаты, единственная строка следует за суммой к оплате.</summary>
    private bool _paymentsEdited;
    private bool _settingPayment;

    public decimal PaidSum => Payments.Sum(p => Math.Max(0, p.Amount));
    public decimal Remainder => Math.Max(0, ToPay - PaidSum);
    /// <summary>Сдача — только розничному покупателю; при выбранном покупателе переплата идёт ему на счёт.</summary>
    public decimal Change => Customer is null ? Math.Max(0, PaidSum - ToPay) : 0;
    /// <summary>Переплата на счёт покупателя: гасит его прежний долг или остаётся предоплатой.</summary>
    public decimal Overpay => Customer is null ? 0 : Math.Max(0, PaidSum - ToPay);
    public bool HasOverpay => Overpay > 0;
    public bool HasChange => Change > 0;
    public decimal DebtAmount => Remainder;
    public bool HasDebt => Remainder > 0;
    public bool CreditNeedsCustomer => HasDebt && Customer is null;

    /// <summary>Окно оформления открыто: если оплат нет — одна строка на всю сумму в основную кассу.</summary>
    private void EnsureDefaultPayment()
    {
        if (Payments.Count > 0 || _paymentsEdited) return;
        AddPaymentRow(CashRegister, ToPay);
    }

    private void SyncAutoPayment()
    {
        if (_paymentsEdited || Payments.Count != 1 || Payments[0].Amount == ToPay) return;
        _settingPayment = true;
        Payments[0].Amount = ToPay;
        _settingPayment = false;
    }

    private PaymentPart AddPaymentRow(CashRegister? cash, decimal amount)
    {
        var part = new PaymentPart { CashRegister = cash, Amount = amount };
        part.PropertyChanged += (_, _) =>
        {
            if (_settingPayment) return;
            _paymentsEdited = true;
            RaiseTotals();
        };
        Payments.Add(part);
        return part;
    }

    /// <summary>«Новая оплата»: строка на неоплаченный остаток, касса — ещё не использованная.</summary>
    [RelayCommand]
    private void AddPayment()
    {
        _paymentsEdited = true;
        var cash = CashRegisters.FirstOrDefault(c => Payments.All(p => p.CashRegister != c)) ?? CashRegister;
        AddPaymentRow(cash, Remainder);
        RaiseTotals();
    }

    [RelayCommand]
    private void RemovePayment(PaymentPart? part)
    {
        if (part is null) return;
        _paymentsEdited = true;
        Payments.Remove(part);
        RaiseTotals();
    }

    private void ClearPayments()
    {
        Payments.Clear();
        _paymentsEdited = false;
    }

    /// <summary>Оплаты для запроса: сдача вычитается с последних строк, нулевые отбрасываются.</summary>
    private IReadOnlyList<Core.Services.SalePayment> PaymentParts()
    {
        var parts = Payments.Where(p => p.CashRegister is not null && p.Amount > 0)
            .Select(p => (Id: p.CashRegister!.Id, Amount: p.Amount)).ToList();
        var extra = Change;
        for (var i = parts.Count - 1; i >= 0 && extra > 0; i--)
        {
            var cut = Math.Min(extra, parts[i].Amount);
            parts[i] = (parts[i].Id, parts[i].Amount - cut);
            extra -= cut;
        }
        return parts.Where(p => p.Amount > 0).Select(p => new Core.Services.SalePayment(p.Id, p.Amount)).ToList();
    }
}
