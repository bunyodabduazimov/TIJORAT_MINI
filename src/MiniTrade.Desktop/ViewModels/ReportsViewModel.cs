using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Пункт выпадающего списка фильтра (null-значение = «все»).</summary>
public sealed record ReportOption<T>(string Name, T Value)
{
    public override string ToString() => Name;
}

/// <summary>
/// Отчёты: продажи (в разрезах), закупки, прибыль, движение денег, остатки.
/// Загружается только открытая вкладка; смена фильтров помечает остальные устаревшими,
/// а незавершённый запрос отменяется новым.
/// </summary>
public partial class ReportsViewModel : PageViewModel
{
    public const int SalesTab = 0, PurchasesTab = 1, ProfitTab = 2, CashTab = 3, StockTab = 4;
    private const int TabCount = 5;

    private readonly ReportService _reports;
    private readonly WarehouseService _warehouse;
    private readonly CatalogService _catalog;
    private readonly bool[] _stale = Enumerable.Repeat(true, TabCount).ToArray();
    private CancellationTokenSource? _cts;
    private bool _suspend;

    public ReportsViewModel(ReportService reports, WarehouseService warehouse, CatalogService catalog, IDialogService dialogs) : base(dialogs)
    {
        _reports = reports;
        _warehouse = warehouse;
        _catalog = catalog;
        SalesPager = new(r => Matches(r.Product));
        PurchasesPager = new(r => Matches(r.Supplier));
        StockPager = new(r => (!OnlyLowStock || r.IsLow || r.IsNegative)
            && (Matches(r.Product) || (!string.IsNullOrWhiteSpace(Search) && (r.Sku?.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase) ?? false))));
        Groupings =
        [
            new(L.T("По товарам"), SalesGrouping.Product),
            new(L.T("По категориям"), SalesGrouping.Category),
            new(L.T("По покупателям"), SalesGrouping.Customer),
            new(L.T("По дням"), SalesGrouping.Day),
            new(L.T("По складам"), SalesGrouping.Warehouse)
        ];
        _grouping = Groupings[0];
    }

    public override string Title => L.T("Аналитика");

    public ObservableCollection<CashFlowRow> CashRows { get; } = new();
    public ObservableCollection<CashCategoryRow> CashCategories { get; } = new();
    /// <summary>Таблицы отчётов с постраничным выводом.</summary>
    public ReportPager<SalesReportRow> SalesPager { get; }
    public ReportPager<PurchaseReportRow> PurchasesPager { get; }
    public ReportPager<StockRow> StockPager { get; }

    public IReadOnlyList<ReportOption<SalesGrouping>> Groupings { get; }
    /// <summary>Справочники для окна фильтра (без пункта «все» — пустое поле означает «все»).</summary>
    private ReportFilterOptions? _options;

    /// <summary>Текущий фильтр отчётов (общий для всех вкладок).</summary>
    public ReportFilter Filter { get; private set; } = ReportFilter.None;

    private ReportFilter CurrentFilter => Filter;

    /// <summary>Пользователь и контрагент — во всех отчётах кроме остатков; товар и категория — кроме движения денег.</summary>
    public bool UserFilterVisible => SelectedTab != StockTab;
    public bool ProductFilterVisible => SelectedTab != CashTab;
    public int ActiveFilterCount =>
        new[] { Filter.WarehouseId, Filter.UserId, Filter.CounterpartyId, Filter.ProductId, Filter.CategoryId }.Count(v => v is not null);
    public bool HasFilters => ActiveFilterCount > 0;

    [RelayCommand]
    private async Task OpenFilterAsync()
    {
        try
        {
            _options ??= await _reports.GetFilterOptionsAsync();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, GetType().Name);
            Dialogs.Error(L.F("Непредвиденная ошибка: {0}", ex.Message));
            return;
        }
        var vm = new ReportsFilterViewModel(Filter, _options, WarehouseFilterVisible, UserFilterVisible, ProductFilterVisible);
        ModalHost.ShowDialog(new Views.FilterWindow { DataContext = vm });
        if (vm.Result is not { } applied || applied == Filter) return;
        Filter = applied;
        FiltersChanged(allTabs: true);
    }

    [RelayCommand]
    private void ResetFilters()
    {
        if (Filter == ReportFilter.None) return;
        Filter = ReportFilter.None;
        FiltersChanged(allTabs: true);
    }

    [ObservableProperty] private DateTime _from = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateTime _to = DateTime.Today;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private ReportOption<SalesGrouping> _grouping;
    [ObservableProperty] private string? _search;
    [ObservableProperty] private bool _onlyLowStock;
    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private ProfitReport? _profit;
    [ObservableProperty] private CashFlowReport? _cashFlow;

    [ObservableProperty] private decimal _salesRevenue;
    [ObservableProperty] private decimal _salesCost;
    [ObservableProperty] private decimal _salesProfit;
    [ObservableProperty] private decimal _salesMargin;
    [ObservableProperty] private int _salesDocuments;
    [ObservableProperty] private decimal _purchasesTotal;
    [ObservableProperty] private decimal _purchasesPaid;
    [ObservableProperty] private decimal _purchasesDebt;
    [ObservableProperty] private decimal _stockCost;
    [ObservableProperty] private decimal _stockSale;
    [ObservableProperty] private decimal _stockMarkup;
    [ObservableProperty] private int _stockLowCount;

    public bool IsProductGrouping => Grouping.Value == SalesGrouping.Product;
    public bool ShowAbc => Grouping.Value is SalesGrouping.Product or SalesGrouping.Category or SalesGrouping.Customer;
    public string PeriodText => L.F("{0:dd.MM.yyyy} — {1:dd.MM.yyyy}", From, To);

    /// <summary>Склад используется в продажах, закупках и остатках.</summary>
    public bool WarehouseFilterVisible => SelectedTab != CashTab;
    public bool PeriodVisible => SelectedTab != StockTab;
    public bool IsSalesTab => SelectedTab == SalesTab;
    public bool IsStockTab => SelectedTab == StockTab;

    /// <summary>Страницы текущей вкладки — панель страниц в общей шапке справа.</summary>
    public object? ActivePager => SelectedTab switch
    {
        SalesTab => SalesPager,
        PurchasesTab => PurchasesPager,
        StockTab => StockPager,
        _ => null,
    };

    partial void OnFromChanged(DateTime value) => FiltersChanged(allTabs: true);
    partial void OnToChanged(DateTime value) => FiltersChanged(allTabs: true);

    partial void OnGroupingChanged(ReportOption<SalesGrouping> value)
    {
        OnPropertyChanged(nameof(IsProductGrouping));
        OnPropertyChanged(nameof(ShowAbc));
        _stale[SalesTab] = true;
        if (!_suspend && SelectedTab == SalesTab) _ = LoadCurrentAsync();
    }

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(WarehouseFilterVisible));
        OnPropertyChanged(nameof(UserFilterVisible));
        OnPropertyChanged(nameof(ProductFilterVisible));
        OnPropertyChanged(nameof(PeriodVisible));
        OnPropertyChanged(nameof(IsSalesTab));
        OnPropertyChanged(nameof(IsStockTab));
        OnPropertyChanged(nameof(ActivePager));
        if (!_suspend) _ = LoadCurrentAsync();
    }

    partial void OnSearchChanged(string? value)
    {
        SalesPager.Refresh();
        PurchasesPager.Refresh();
        StockPager.Refresh();
    }

    partial void OnOnlyLowStockChanged(bool value) => StockPager.Refresh();

    private void FiltersChanged(bool allTabs)
    {
        OnPropertyChanged(nameof(PeriodText));
        OnPropertyChanged(nameof(HasFilters));
        OnPropertyChanged(nameof(ActiveFilterCount));
        if (allTabs) Array.Fill(_stale, true);
        if (!_suspend) _ = LoadCurrentAsync();
    }

    private bool Matches(string text) =>
        string.IsNullOrWhiteSpace(Search) || text.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase);

    public override async Task LoadAsync()
    {
        try
        {
            // Справочники могли измениться в других модулях
            _options = await _reports.GetFilterOptionsAsync();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, GetType().Name);
            Dialogs.Error(L.F("Непредвиденная ошибка: {0}", ex.Message));
        }
        // При открытии страницы данные могли измениться в других модулях
        Array.Fill(_stale, true);
        await LoadCurrentAsync();
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        Array.Fill(_stale, true);
        return LoadCurrentAsync();
    }

    [RelayCommand]
    private void SetPeriod(string period)
    {
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var (from, to) = period switch
        {
            "today" => (today, today),
            "yesterday" => (today.AddDays(-1), today.AddDays(-1)),
            "week" => (today.AddDays(-6), today),
            "month" => (monthStart, today),
            "prevmonth" => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            "quarter" => (new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1), today),
            "year" => (new DateTime(today.Year, 1, 1), today),
            _ => (From, To)
        };
        _suspend = true;
        From = from;
        To = to;
        _suspend = false;
        FiltersChanged(allTabs: true);
    }

    /// <summary>Загружает отчёт открытой вкладки, если он устарел. Предыдущий запрос отменяется.</summary>
    private async Task LoadCurrentAsync()
    {
        var tab = SelectedTab;
        if (tab is < 0 or >= TabCount || !_stale[tab]) return;

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        var (from, to) = From <= To ? (From, To) : (To, From);
        var filter = CurrentFilter;

        IsLoading = true;
        try
        {
            switch (tab)
            {
                case SalesTab:
                {
                    var rows = await _reports.GetSalesAsync(from, to, Grouping.Value, filter, ct);
                    ct.ThrowIfCancellationRequested();
                    SalesPager.SetSource(rows.Select(Localize).ToList());
                    SalesRevenue = rows.Sum(r => r.Revenue);
                    SalesCost = rows.Sum(r => r.Cost);
                    SalesProfit = SalesRevenue - SalesCost;
                    SalesMargin = SalesRevenue == 0 ? 0 : Math.Round(SalesProfit / SalesRevenue * 100, 1);
                    SalesDocuments = Grouping.Value is SalesGrouping.Day or SalesGrouping.Warehouse or SalesGrouping.Customer
                        ? rows.Sum(r => r.Documents) : 0;
                    break;
                }
                case PurchasesTab:
                {
                    var rows = await _reports.GetPurchasesBySupplierAsync(from, to, filter, ct);
                    ct.ThrowIfCancellationRequested();
                    PurchasesPager.SetSource(rows);
                    PurchasesTotal = rows.Sum(r => r.Total);
                    PurchasesPaid = rows.Sum(r => r.Paid);
                    PurchasesDebt = rows.Sum(r => r.Debt);
                    break;
                }
                case ProfitTab:
                {
                    var p = await _reports.GetProfitAsync(from, to, filter, ct);
                    ct.ThrowIfCancellationRequested();
                    Profit = p;
                    break;
                }
                case CashTab:
                {
                    var c = await _reports.GetCashFlowAsync(from, to, filter, ct);
                    ct.ThrowIfCancellationRequested();
                    CashFlow = c;
                    Fill(CashRows, c.Registers);
                    Fill(CashCategories, c.Categories.Select(x => x with { Category = L.T(x.Category) }));
                    break;
                }
                case StockTab:
                {
                    var rows = await _warehouse.GetBalancesAsync(filter.WarehouseId, ct: ct, productId: filter.ProductId, categoryId: filter.CategoryId);
                    ct.ThrowIfCancellationRequested();
                    StockPager.SetSource(rows);
                    StockCost = rows.Sum(r => r.CostValue);
                    StockSale = rows.Sum(r => r.SaleValue);
                    StockMarkup = StockSale - StockCost;
                    StockLowCount = rows.Count(r => r.IsLow || r.IsNegative);
                    break;
                }
            }
            _stale[tab] = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Запрос заменён более новым
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, GetType().Name);
            Dialogs.Error(L.F("Непредвиденная ошибка: {0}", ex.Message));
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsLoading = false;
                _cts = null;
            }
            cts.Dispose();
        }
    }

    private SalesReportRow Localize(SalesReportRow r) => Grouping.Value switch
    {
        SalesGrouping.Category when r.Product == ReportService.NoCategory => r with { Product = L.T(r.Product) },
        SalesGrouping.Customer when r.Product == ReportService.RetailCustomer => r with { Product = L.T(r.Product) },
        _ => r
    };

    /// <summary>Экспорт открытого отчёта в Excel (с учётом поиска).</summary>
    [RelayCommand]
    private void Export()
    {
        var (name, lines) = SelectedTab switch
        {
            SalesTab => (L.T("Анализ продаж") + " - " + Grouping.Name, SalesLines()),
            PurchasesTab => (L.T("Анализ закупок"), PurchaseLines()),
            ProfitTab => (L.T("Прибыль и рентабельность"), ProfitLines()),
            CashTab => (L.T("Денежные потоки"), CashLines()),
            _ => (L.T("Анализ запасов"), StockLines())
        };
        var safeName = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        var suffix = SelectedTab == StockTab ? DateTime.Today.ToString("yyyyMMdd") : $"{From:yyyyMMdd}-{To:yyyyMMdd}";
        var path = Dialogs.SaveFile($"{safeName}_{suffix}.xlsx", ExcelExport.Filter);
        if (path is null) return;
        try
        {
            ExcelExport.Save(path, lines.Prepend(Row(name, SelectedTab == StockTab ? DateTime.Today.ToString("dd.MM.yyyy") : PeriodText)), name);
            Dialogs.Info(L.F("Отчёт сохранён: {0}", path));
        }
        catch (IOException ex)
        {
            Dialogs.Error(L.F("Не удалось сохранить файл: {0}", ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            Dialogs.Error(L.F("Не удалось сохранить файл: {0}", ex.Message));
        }
    }

    private IEnumerable<string> SalesLines()
    {
        var rows = SalesPager.Filtered;
        yield return L.T("Наименование;Ед.;Количество;Чеков;Выручка;Себестоимость;Прибыль;Маржа %;Доля %;ABC");
        foreach (var r in rows)
            yield return Row(r.Product, r.Unit, IsProductGrouping ? r.Quantity : null, r.Documents, r.Revenue, r.Cost, r.Profit, r.MarginPercent, r.SharePercent, r.Abc);
        var rev = rows.Sum(r => r.Revenue);
        var cost = rows.Sum(r => r.Cost);
        yield return Row(L.T("Итого"), null, null, null, rev, cost, rev - cost, rev == 0 ? 0 : Math.Round((rev - cost) / rev * 100, 1), null, null);
    }

    private IEnumerable<string> PurchaseLines()
    {
        var rows = PurchasesPager.Filtered;
        yield return L.T("Поставщик;Документов;Сумма;Оплачено;Долг");
        foreach (var r in rows) yield return Row(r.Supplier, r.Documents, r.Total, r.Paid, r.Debt);
        yield return Row(L.T("Итого"), rows.Sum(r => r.Documents), rows.Sum(r => r.Total), rows.Sum(r => r.Paid), rows.Sum(r => r.Debt));
    }

    private IEnumerable<string> ProfitLines()
    {
        if (Profit is not { } p) yield break;
        yield return L.T("Показатель;Сумма");
        yield return Row(L.T("Выручка"), p.Revenue);
        yield return Row(L.T("Возвраты"), p.Returns);
        yield return Row(L.T("Чистая выручка"), p.NetRevenue);
        yield return Row(L.T("Себестоимость продаж"), p.CostOfSales);
        yield return Row(L.T("Валовая прибыль"), p.GrossProfit);
        foreach (var e in p.ExpenseItems) yield return Row(L.T("Расход: ") + e.Category, e.Amount);
        yield return Row(L.T("Операционные расходы"), p.Expenses);
        yield return Row(L.T("Чистая прибыль"), p.NetProfit);
        yield return Row(L.T("Чеков"), (decimal)p.Checks);
        yield return Row(L.T("Средний чек"), p.AverageCheck);
        yield return Row(L.T("Валовая маржа, %"), p.GrossMarginPercent);
        yield return Row(L.T("Рентабельность, %"), p.NetMarginPercent);
    }

    private IEnumerable<string> CashLines()
    {
        if (CashFlow is not { } c) yield break;
        yield return L.T("Касса;На начало;Приход;Расход;На конец");
        foreach (var r in c.Registers) yield return Row(r.CashRegister, r.Opening, r.Income, r.Expense, r.Closing);
        yield return Row(L.T("Итого"), c.Opening, c.Income, c.Expense, c.Closing);
        yield return "";
        yield return L.T("Направление;Статья;Операций;Сумма");
        foreach (var x in CashCategories) yield return Row(x.IsIncome ? L.T("Приход") : L.T("Расход"), x.Category, x.Operations, x.Amount);
    }

    private IEnumerable<string> StockLines()
    {
        var rows = StockPager.Filtered;
        yield return L.T("Товар;Артикул;Категория;Склад;Количество;Ед.;Себестоимость;Сумма по себест.;Цена;Сумма по цене;Мин. остаток");
        foreach (var r in rows)
            yield return Row(r.Product, r.Sku, r.Category, r.Warehouse, r.Quantity, r.Unit, r.AvgCost, r.CostValue, r.SalePrice, r.SaleValue, r.MinStock);
        yield return Row(L.T("Итого"), null, null, null, null, null, null, rows.Sum(r => r.CostValue), null, rows.Sum(r => r.SaleValue), null);
    }

    private static readonly CultureInfo CsvCulture = CultureInfo.GetCultureInfo("ru-RU");

    private static string Row(params object?[] values) => string.Join(';', values.Select(v => v switch
    {
        decimal d => d.ToString("0.###", CsvCulture),
        int i => i.ToString(CsvCulture),
        null => "",
        _ => "\"" + v.ToString()!.Replace("\"", "\"\"") + "\""
    }));
}

/// <summary>Модель окна «Фильтр» отчётов: пустое поле — «все», «×» в поле сбрасывает выбор.</summary>
public partial class ReportsFilterViewModel : ObservableObject, ICloseRequest
{
    public ReportsFilterViewModel(ReportFilter current, ReportFilterOptions options,
        bool warehouseVisible, bool userVisible, bool productVisible)
    {
        static IReadOnlyList<ReportOption<int?>> Map(IEnumerable<ReportLookup> items) =>
            items.Select(i => new ReportOption<int?>(i.Name, i.Id)).ToList();
        Warehouses = Map(options.Warehouses);
        Users = Map(options.Users);
        Counterparties = Map(options.Counterparties);
        Products = Map(options.Products);
        Categories = Map(options.Categories);
        WarehouseVisible = warehouseVisible;
        UserVisible = userVisible;
        ProductVisible = productVisible;
        Load(current);
    }

    public string Title => L.T("Фильтр");
    public double DialogWidth => 480;
    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<ReportOption<int?>> Warehouses { get; }
    public IReadOnlyList<ReportOption<int?>> Users { get; }
    public IReadOnlyList<ReportOption<int?>> Counterparties { get; }
    public IReadOnlyList<ReportOption<int?>> Products { get; }
    public IReadOnlyList<ReportOption<int?>> Categories { get; }

    /// <summary>Поля, которые учитывает открытый отчёт.</summary>
    public bool WarehouseVisible { get; }
    public bool UserVisible { get; }
    public bool ProductVisible { get; }

    [ObservableProperty] private ReportOption<int?>? _warehouse;
    [ObservableProperty] private ReportOption<int?>? _user;
    [ObservableProperty] private ReportOption<int?>? _counterparty;
    [ObservableProperty] private ReportOption<int?>? _product;
    [ObservableProperty] private ReportOption<int?>? _category;

    public ReportFilter? Result { get; private set; }

    private static ReportOption<int?>? Find(IReadOnlyList<ReportOption<int?>> list, int? id) =>
        id is null ? null : list.FirstOrDefault(o => o.Value == id);

    private void Load(ReportFilter f)
    {
        Warehouse = Find(Warehouses, f.WarehouseId);
        User = Find(Users, f.UserId);
        Counterparty = Find(Counterparties, f.CounterpartyId);
        Product = Find(Products, f.ProductId);
        Category = Find(Categories, f.CategoryId);
    }

    [RelayCommand]
    private void Reset() => Load(ReportFilter.None);

    [RelayCommand]
    private void Apply()
    {
        Result = new ReportFilter(Warehouse?.Value, User?.Value, Counterparty?.Value, Product?.Value, Category?.Value);
        CloseRequested?.Invoke(this, true);
    }
}
