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

    public bool IsOut => Stock <= 0;
    public bool InCart => InCartQuantity > 0;
    public string StockText => L.F("{0:0.###} {1}", Stock, Product.Unit);
}

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
    [ObservableProperty] private bool _showTable;
    [ObservableProperty] private bool _onlyInStock;
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
        _allItems.AddRange(Products.Select(p => new PosItem(p)));
        await LoadStockAsync();

        var selectedId = SelectedPosCategory?.Category?.Id;
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
        if (_allItems.Count > 0) _ = RunAsync(async () => { await LoadStockAsync(); RefreshShowcase(); });
    }

    partial void OnSearchTextChanged(string? value) => RefreshShowcase();
    partial void OnSelectedPosCategoryChanged(PosCategory? value) => RefreshShowcase();
    partial void OnOnlyInStockChanged(bool value) => RefreshShowcase();

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
        var product = exact ?? SelectedItem?.Product ?? Showcase.FirstOrDefault()?.Product;
        if (product is null)
        {
            if (!Dialogs.Confirm(L.F("Товар «{0}» не найден.\nСоздать новый товар?", term))) return;
            var looksLikeCode = term.All(char.IsLetterOrDigit) && term.Any(char.IsDigit);
            var created = await CatalogDialogs.EditAsync(Products, looksLikeCode ? new Product { Barcode = term } : new Product { Name = term });
            if (created?.Saved is not { } p) return;
            _allItems.Add(new PosItem(p) { Stock = _stock.GetValueOrDefault(p.Id) });
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

    private void AddToCart(Product product)
    {
        AddOrMerge(product, 1, DefaultPrice(product));
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
        DiscountPercent = 0;
        Received = 0;
        OnCredit = false;
        ExtraPayments.Clear();
        Comment = null;
        Customer = null;
        CurrentDraftId = null;
        EditingSale = null;
        LastResult = null;
        RaiseTotals();
        FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>«Без сдачи»: получено ровно к оплате.</summary>
    [RelayCommand]
    private void ExactCash() => Received = Math.Max(0, MainPart);

    /// <summary>Быстрые купюры: прибавляют сумму к «Получено».</summary>
    [RelayCommand]
    private void AddCash(object? amount)
    {
        if (amount is null) return;
        if (decimal.TryParse(Convert.ToString(amount, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var a))
            Received += a;
    }

    [RelayCommand]
    private void ResetReceived() => Received = 0;

    public IReadOnlyList<int> Banknotes { get; } = [10, 20, 50, 100, 200, 500];

    // ── Итоги: скидка %, получено, сдача ──
    [ObservableProperty] private decimal _discountPercent;
    [ObservableProperty] private decimal _received;

    public int ItemsCount => Lines.Count;
    public bool IsCartEmpty => Lines.Count == 0;
    public decimal ItemsQuantity => Lines.Sum(l => l.Quantity);
    /// <summary>Цена строки со скидкой (скидка % применяется к каждой цене — так она попадает в документ).</summary>
    private decimal Discounted(decimal price) => Money.Round(price * (100 - Math.Clamp(DiscountPercent, 0, 100)) / 100);
    public decimal ToPay => Lines.Sum(l => Money.Round(l.Quantity * Discounted(l.Price)));
    public decimal DiscountAmount => Total - ToPay;
    public bool HasDiscount => DiscountAmount > 0;
    public decimal Change => Received > MainPart ? Money.Round(Received - MainPart) : 0;
    public bool NotEnoughReceived => !OnCredit && Received > 0 && Received < MainPart;

    // ── Продажа в долг: в основную кассу поступает только «Получено», остаток записывается на покупателя ──
    [ObservableProperty] private bool _onCredit;
    /// <summary>Сколько реально поступает в основную кассу.</summary>
    public decimal MainPaid => !OnCredit ? Math.Max(0, MainPart) : Math.Clamp(Received, 0, Math.Max(0, MainPart));
    public decimal DebtAmount => OnCredit ? Math.Max(0, MainPart - MainPaid) : 0;
    public bool HasDebt => DebtAmount > 0;
    public bool CreditNeedsCustomer => OnCredit && Customer is null;
    /// <summary>Получено меньше — подсказка, что остаток можно записать в долг.</summary>
    public bool CanOfferCredit => NotEnoughReceived;

    partial void OnDiscountPercentChanged(decimal value) => RaiseTotals();
    partial void OnReceivedChanged(decimal value) => RaiseTotals();
    partial void OnOnCreditChanged(bool value) => RaiseTotals();
    partial void OnCustomerChanged(Counterparty? value) => RaiseTotals();

    /// <summary>«В долг» одним нажатием: всё неоплаченное — на покупателя.</summary>
    [RelayCommand]
    private void ToggleCredit() => OnCredit = !OnCredit;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Total)) RaiseTotals();
    }

    private void RaiseTotals()
    {
        foreach (var n in new[] { nameof(ItemsCount), nameof(IsCartEmpty), nameof(ItemsQuantity), nameof(ToPay), nameof(DiscountAmount), nameof(HasDiscount), nameof(Change), nameof(NotEnoughReceived), nameof(HasExtraPayments), nameof(ExtraPaid), nameof(MainPart), nameof(ExtraOverpaid), nameof(MainPaid), nameof(DebtAmount), nameof(HasDebt), nameof(CreditNeedsCustomer), nameof(CanOfferCredit) })
            OnPropertyChanged(n);
        SyncCartMarks();
    }

    /// <summary>Итог последней продажи — зелёная плашка над чеком вместо окна-сообщения.</summary>
    [ObservableProperty] private string? _lastResult;

    private IReadOnlyList<Core.Services.DocumentLine> CartLines() =>
        Lines.Select(l => new Core.Services.DocumentLine(l.Product.Id, l.Quantity, Discounted(l.Price))).ToList();
}

/// <summary>Доп. оплата на другую кассу (смешанная оплата).</summary>
public partial class PaymentPart : ObservableObject
{
    [ObservableProperty] private CashRegister? _cashRegister;
    [ObservableProperty] private decimal _amount;
}

public partial class SalesViewModel
{
    /// <summary>Доп. оплаты; остаток суммы идёт в основную кассу (CashRegister), к нему же относятся «Получено» и сдача.</summary>
    public ObservableCollection<PaymentPart> ExtraPayments { get; } = new();
    public bool HasExtraPayments => ExtraPayments.Count > 0;
    public decimal ExtraPaid => ExtraPayments.Sum(p => p.Amount);
    /// <summary>Сколько приходится на основную кассу.</summary>
    public decimal MainPart => ToPay - ExtraPaid;
    public bool ExtraOverpaid => ExtraPaid > ToPay;

    [RelayCommand]
    private void AddPayment()
    {
        var part = new PaymentPart
        {
            CashRegister = CashRegisters.FirstOrDefault(c => c != CashRegister) ?? CashRegister,
            Amount = Math.Max(0, MainPart)
        };
        part.PropertyChanged += (_, _) => RaiseTotals();
        ExtraPayments.Add(part);
        Received = 0;
        RaiseTotals();
    }

    [RelayCommand]
    private void RemovePayment(PaymentPart? part)
    {
        if (part is null) return;
        ExtraPayments.Remove(part);
        RaiseTotals();
    }

    private void ClearPayments()
    {
        ExtraPayments.Clear();
        RaiseTotals();
    }

    /// <summary>Части оплаты для запроса; null — всё в основную кассу.</summary>
    private IReadOnlyList<Core.Services.SalePayment>? PaymentParts()
    {
        if ((ExtraPayments.Count == 0 && !OnCredit) || CashRegister is null) return null;
        var list = ExtraPayments.Where(p => p.CashRegister is not null && p.Amount > 0)
            .Select(p => new Core.Services.SalePayment(p.CashRegister!.Id, p.Amount)).ToList();
        if (MainPaid > 0) list.Insert(0, new Core.Services.SalePayment(CashRegister.Id, MainPaid));
        return list;
    }
}
