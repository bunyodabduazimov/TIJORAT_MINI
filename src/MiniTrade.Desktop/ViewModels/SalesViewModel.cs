using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.Views;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Продажи: оформление (и изменение проведённых), журнал продаж, возвраты.</summary>
public partial class SalesViewModel(SalesService sales, CatalogService catalog, WarehouseService warehouses, SettingsService settings,
    IDialogService dialogs, ICatalogDialogs catalogDialogs)
    : DocumentEditorBase(dialogs, catalogDialogs)
{
    public override string Title => L.T("Продажи");

    public ObservableCollection<Warehouse> Warehouses { get; } = new();
    public ObservableCollection<CashRegister> CashRegisters { get; } = new();
    public ObservableCollection<Counterparty> Customers { get; } = new();
    public ObservableCollection<Sale> History { get; } = new();
    public ObservableCollection<SaleReturn> Returns { get; } = new();

    [ObservableProperty] private Warehouse? _warehouse;
    [ObservableProperty] private CashRegister? _cashRegister;
    [ObservableProperty] private Counterparty? _customer;
    [ObservableProperty] private string? _comment;

    /// <summary>Открытая вкладка: 0 — новая продажа, 1 — история, 2 — возвраты.</summary>
    // Без права оформления открываем сразу историю (вкладка оформления скрыта)
    [ObservableProperty] private int _selectedTab = Core.Services.Access.Can(Core.Domain.Perm.SalesCreate) || Core.Services.Access.Can(Core.Domain.Perm.SalesEdit) ? 0 : 1;

    protected override decimal DefaultPrice(Product product) => product.SalePrice;

    // Справочники прямо из чека
    [RelayCommand]
    private async Task AddCustomerAsync()
    {
        if (await CatalogDialogs.EditAsync(Customers) is { Saved: { } c }) Customer = c;
    }

    [RelayCommand]
    private async Task EditCustomerAsync()
    {
        if (Customer is null) { Dialogs.Error(L.T("Выберите покупателя.")); return; }
        if (await CatalogDialogs.EditAsync(Customers, Customer) is { } r) Customer = r.Saved;
    }

    private bool _loaded;

    public override Task LoadAsync() => RunAsync(async () =>
    {
        SetProducts(await catalog.GetAllAsync<Product>(activeOnly: true));
        ScalePrefix = (await settings.GetCompanyAsync()).ScalePrefix;
        Fill(Warehouses, await catalog.GetAllAsync<Warehouse>(true));
        Fill(CashRegisters, await catalog.GetAllAsync<CashRegister>(true));
        Fill(Customers, await catalog.GetAllAsync<Counterparty>(true));
        Warehouse ??= Warehouses.FirstOrDefault(w => w.Id == App.Ui.PosWarehouseId) ?? Warehouses.FirstOrDefault();
        CashRegister ??= CashRegisters.FirstOrDefault();
        await LoadShowcaseAsync();
        LoadDrafts();
        await LoadHistoryCore();
        _loaded = true;
    });

    // ── Изменение проведённой продажи: документ открывается во вкладке оформления ──

    /// <summary>Продажа, которую сейчас изменяют (null — оформляется новая).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingSale), nameof(EditorTabTitle))]
    private Sale? _editingSale;

    public bool IsEditingSale => EditingSale is not null;
    public string EditorTabTitle => EditingSale is { } s ? L.F("Изменение {0}", s.Number) : L.T("Касса (POS)");

    [RelayCommand]
    private async Task PostSaleAsync()
    {
        if (Warehouse is null || CashRegister is null) { Dialogs.Error(L.T("Выберите склад и кассу.")); return; }
        if (Lines.Count == 0) { Dialogs.Error(L.T("Добавьте товары в чек.")); return; }
        if (Payments.Any(p => p.CashRegister is null && p.Amount > 0)) { Dialogs.Error(L.T("Выберите кассу для каждой оплаты.")); return; }
        if (Payments.Any(p => p.Amount < 0)) { Dialogs.Error(L.T("Сумма оплаты не может быть отрицательной.")); return; }
        if (CreditNeedsCustomer)
        {
            Dialogs.Error(L.F("Оплачено меньше суммы к оплате (остаток {0}). Чтобы продать в долг, выберите покупателя.", Core.Common.Money.Format(Remainder)));
            return;
        }
        var debt = DebtAmount;
        var mainCash = Payments.FirstOrDefault(p => p.CashRegister is not null && p.Amount > 0)?.CashRegister ?? CashRegister;
        var request = new SaleRequest(Warehouse.Id, mainCash.Id, CartLines(), Customer?.Id, Comment, Payments: PaymentParts(), OnCredit: HasDebt);
        var editing = EditingSale;
        Sale? sale = null;
        var ok = await RunAsync(async () =>
        {
            sale = editing is null ? await sales.CreateSaleAsync(request) : await sales.UpdateSaleAsync(editing.Id, request);
        });
        if (!ok || sale is null) return;
        var change = Change;

        // Фискальный чек на ККТ — только для новой полностью оплаченной продажи
        string? fiscalError = null;
        if (editing is null && FiscalPrinter.Enabled)
        {
            if (debt > 0)
                fiscalError = L.T("Продажа в долг — чек ККТ не пробит.");
            else
            {
                var payments = request.Payments ?? [];
                try
                {
                    var full = await sales.GetSaleAsync(sale.Id);
                    if (full is not null) fiscalError = await FiscalPrinter.SaleAsync(full, payments, change, CashRegisters);
                }
                catch (Exception ex) { ErrorLog.Write(ex, GetType().Name); fiscalError = ex.Message; }

                // Чек на ККТ не пробит — продажу не сохраняем: откатываем её, корзина остаётся для повтора
                if (fiscalError is not null)
                {
                    var err = fiscalError;
                    var rolledBack = await RunAsync(() => sales.DeleteSaleAsync(sale.Id));
                    Dialogs.Error(rolledBack
                        ? L.F("Чек на ККТ не пробит — продажа не сохранена:\n{0}", err)
                        : L.F("Чек на ККТ не пробит, но продажу {0} отменить не удалось — удалите её вручную:\n{1}", sale.Number, err));
                    if (!rolledBack) await RunAsync(LoadHistoryCore);
                    return;
                }
            }
        }

        // Без окна-сообщения: итог — плашкой над корзиной, фокус сразу в поиск для следующего покупателя
        RemoveCurrentDraft();
        NewSale();
        SalePosted?.Invoke(this, EventArgs.Empty);
        LastResult = editing is not null
            ? L.F("Продажа {0} изменена: {1}", sale.Number, Core.Common.Money.Format(sale.Total))
            : debt > 0
                ? L.F("Продажа {0} проведена: {1}. В долг: {2}", sale.Number, Core.Common.Money.Format(sale.Total), Core.Common.Money.Format(debt))
            : change > 0
                ? L.F("Продажа {0} проведена: {1}. Сдача: {2}", sale.Number, Core.Common.Money.Format(sale.Total), Core.Common.Money.Format(change))
                : L.F("Продажа {0} проведена: {1}", sale.Number, Core.Common.Money.Format(sale.Total));
        await RunAsync(async () => { await LoadStockAsync(); RefreshShowcase(); await LoadHistoryCore(); });
        if (editing is not null) SelectedTab = 1;
        if (fiscalError is not null)
            Dialogs.Error(L.F("Продажа {0} проведена, но чек на ККТ не пробит:\n{1}", sale.Number, fiscalError));
    }

    /// <summary>Загрузить строки и реквизиты продажи в чек. asEdit — изменение этой продажи, иначе — новая копия.</summary>
    private async Task OpenInEditorAsync(Sale? row, bool asEdit)
    {
        if (row is null) return;
        Sale? sale = null;
        if (!await RunAsync(async () => sale = await sales.GetSaleAsync(row.Id)) || sale is null) return;
        if (asEdit && sale.ReturnedTotal > 0)
        {
            Dialogs.Error(L.F("По продаже {0} оформлен возврат — изменить её нельзя.", sale.Number));
            return;
        }
        // Текущий непустой чек не теряем — откладываем в черновики
        if (EditingSale is null) StoreCurrentAsDraft();
        NewSale();

        var missing = 0;
        foreach (var i in sale.Items)
        {
            var product = Products.FirstOrDefault(p => p.Id == i.ProductId);
            if (product is null && asEdit && i.Product is not null) { product = i.Product; Products.Add(product); }
            if (product is null) { missing++; continue; }
            AddOrMerge(product, i.Quantity, i.Price);
        }
        Warehouse = Warehouses.FirstOrDefault(w => w.Id == sale.WarehouseId) ?? Warehouse;
        CashRegister = CashRegisters.FirstOrDefault(c => c.Id == sale.CashRegisterId) ?? CashRegister;
        Customer = sale.CustomerId is { } cu ? Customers.FirstOrDefault(c => c.Id == cu) : null;
        Comment = sale.Comment;
        EditingSale = asEdit ? sale : null;
        // Изменение продажи в долг: сохраняем ту же оплату (остаток снова уйдёт в долг)
        if (asEdit && sale.PaidAmount < sale.Total)
        {
            _paymentsEdited = true;
            if (sale.PaidAmount > 0) AddPaymentRow(CashRegister, sale.PaidAmount);
        }
        RaiseTotals();
        SelectedTab = 0;
        if (missing > 0)
            Dialogs.Error(L.F("Товаров из продажи больше нет в справочнике: {0}. Они не добавлены в чек.", missing));
    }

    [RelayCommand]
    private void CancelEdit()
    {
        NewSale();
        SelectedTab = 1;
    }

    // ── Журнал продаж: период, поиск, фильтр, страницы ──

    [ObservableProperty] private DateTime _from = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime _to = DateTime.Today;
    partial void OnFromChanged(DateTime value) => ReloadFromFirstPage();
    partial void OnToChanged(DateTime value) => ReloadFromFirstPage();

    [ObservableProperty] private decimal _periodTotal;
    [ObservableProperty] private decimal _periodReturned;
    public decimal PeriodNet => PeriodTotal - PeriodReturned;
    partial void OnPeriodTotalChanged(decimal value) => OnPropertyChanged(nameof(PeriodNet));
    partial void OnPeriodReturnedChanged(decimal value) => OnPropertyChanged(nameof(PeriodNet));

    [ObservableProperty] private string? _historySearch;
    private CancellationTokenSource? _searchDelay;

    partial void OnHistorySearchChanged(string? value)
    {
        _searchDelay?.Cancel();
        var cts = _searchDelay = new CancellationTokenSource();
        _ = Task.Delay(300, cts.Token).ContinueWith(_ => ReloadFromFirstPage(), cts.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public SalesFilter Filter { get; private set; } = new();
    public bool HasActiveFilter => Filter.ActiveCount > 0;
    public int ActiveFilterCount => Filter.ActiveCount;

    public IReadOnlyList<int> PageSizes { get; } = [25, 50, 100, 200];
    [ObservableProperty] private int _pageSize = 50;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _totalCount;
    public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    public string PageInfo => TotalCount == 0
        ? L.T("Ничего не найдено")
        : L.F("Показано {0}–{1} из {2:N0}", (Page - 1) * PageSize + 1, (Page - 1) * PageSize + History.Count, TotalCount);
    public string PageLabel => L.F("{0} из {1}", Page, PageCount);
    partial void OnPageSizeChanged(int value) => ReloadFromFirstPage();

    private int _loadVersion;

    private void ReloadFromFirstPage()
    {
        if (!_loaded) return;
        Page = 1;
        _ = RunAsync(LoadHistoryCore);
    }

    [RelayCommand]
    private Task LoadHistoryAsync() => RunAsync(LoadHistoryCore);

    private async Task LoadHistoryCore()
    {
        var version = ++_loadVersion;
        var f = new SaleFilter(From, To, Filter.WarehouseId, Filter.CashRegisterId, Filter.CustomerId, HistorySearch);
        var result = await sales.GetSalesPageAsync(f, (Page - 1) * PageSize, PageSize);
        var returns = await sales.GetReturnsAsync(From, To);
        if (version != _loadVersion) return; // ответ на устаревший запрос

        if (result.Items.Count == 0 && result.TotalCount > 0 && Page > 1)
        {
            Page = Math.Max(1, (result.TotalCount + PageSize - 1) / PageSize);
            await LoadHistoryCore();
            return;
        }

        Fill(History, result.Items);
        Fill(Returns, returns);
        TotalCount = result.TotalCount;
        PeriodTotal = result.Total;
        PeriodReturned = result.Returned;
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageInfo));
        OnPropertyChanged(nameof(PageLabel));
        FirstPageCommand.NotifyCanExecuteChanged();
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
    }

    private bool CanGoBack => Page > 1;
    private bool CanGoForward => Page < PageCount;
    private Task GoToAsync(int page) { Page = page; return RunAsync(LoadHistoryCore); }

    [RelayCommand(CanExecute = nameof(CanGoBack))] private Task FirstPageAsync() => GoToAsync(1);
    [RelayCommand(CanExecute = nameof(CanGoBack))] private Task PrevPageAsync() => GoToAsync(Page - 1);
    [RelayCommand(CanExecute = nameof(CanGoForward))] private Task NextPageAsync() => GoToAsync(Page + 1);
    [RelayCommand(CanExecute = nameof(CanGoForward))] private Task LastPageAsync() => GoToAsync(PageCount);

    [RelayCommand]
    private async Task OpenFilterAsync()
    {
        var vm = new SalesFilterViewModel(Filter, Warehouses, CashRegisters, await catalog.GetAllAsync<Counterparty>());
        ModalHost.ShowDialog(new FilterWindow { DataContext = vm });
        if (vm.Result is not { } applied) return;
        Filter = applied;
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(ActiveFilterCount));
        Page = 1;
        await RunAsync(LoadHistoryCore);
    }

    // ── Меню строки журнала: изменить, скопировать, печать, возврат, удалить ──

    private static bool CanModifySale(Sale? s) => s is not null && s.ReturnedTotal == 0;
    private static bool CanReturnSale(Sale? s) => s is not null && s.ReturnedTotal < s.Total;

    [RelayCommand(CanExecute = nameof(CanModifySale))]
    private Task EditSaleAsync(Sale? s) => OpenInEditorAsync(s, asEdit: true);

    [RelayCommand]
    private Task CopySaleAsync(Sale? s) => OpenInEditorAsync(s, asEdit: false);

    /// <summary>Двойной щелчок по строке: изменить, если можно, иначе — копия.</summary>
    [RelayCommand]
    private Task OpenSaleAsync(Sale? s) => CanModifySale(s) ? EditSaleAsync(s) : CopySaleAsync(s);

    [RelayCommand]
    private async Task PrintSaleAsync(Sale? s)
    {
        if (s is null) return;
        await RunAsync(async () =>
        {
            if (await sales.GetSaleAsync(s.Id) is { } full) SaleReceiptPrinter.Print(full, await settings.GetCompanyAsync());
        });
    }

    [RelayCommand]
    private async Task PrintInvoiceAsync(Sale? s)
    {
        if (s is null) return;
        await RunAsync(async () =>
        {
            if (await sales.GetSaleAsync(s.Id) is { } full) SaleReceiptPrinter.PrintInvoice(full, await settings.GetCompanyAsync());
        });
    }

    [RelayCommand(CanExecute = nameof(CanReturnSale))]
    private async Task ReturnSaleAsync(Sale? s)
    {
        if (s is null) return;
        Sale? full = null;
        if (!await RunAsync(async () => full = await sales.GetSaleAsync(s.Id)) || full is null) return;
        var vm = new SaleReturnViewModel(full, sales, Dialogs, CashRegisters);
        if (vm.Lines.Count == 0) { Dialogs.Info(L.F("По продаже {0} весь товар уже возвращён.", full.Number)); return; }
        ModalHost.ShowDialog(new SaleReturnWindow { DataContext = vm });
        if (vm.Posted) await RunAsync(async () => { await LoadStockAsync(); RefreshShowcase(); await LoadHistoryCore(); });
    }

    [RelayCommand(CanExecute = nameof(CanModifySale))]
    private async Task DeleteSaleAsync(Sale? s)
    {
        if (s is null || !Dialogs.Confirm(L.F("Удалить продажу {0} на сумму {1}?\nТовар вернётся на склад, сумма будет снята с кассы.",
                s.Number, Core.Common.Money.Format(s.Total)))) return;
        if (!await RunAsync(() => sales.DeleteSaleAsync(s.Id))) return;
        if (EditingSale?.Id == s.Id) NewSale();
        await RunAsync(async () => { await LoadStockAsync(); RefreshShowcase(); await LoadHistoryCore(); });
    }
}

/// <summary>Условия фильтра журнала продаж (кроме периода и поиска).</summary>
public sealed record SalesFilter(int? WarehouseId = null, int? CashRegisterId = null, int? CustomerId = null)
{
    public int ActiveCount => (WarehouseId is null ? 0 : 1) + (CashRegisterId is null ? 0 : 1) + (CustomerId is null ? 0 : 1);
}

/// <summary>Модель окна «Фильтр» журнала продаж.</summary>
public partial class SalesFilterViewModel : ObservableObject, ICloseRequest
{
    public SalesFilterViewModel(SalesFilter current, IEnumerable<Warehouse> warehouses, IEnumerable<CashRegister> cash, IEnumerable<Counterparty> customers)
    {
        WarehouseChoices = [new(L.T("Все склады"), null), .. warehouses.Select(w => new FilterChoice<int>(w.Name, w.Id))];
        CashChoices = [new(L.T("Все кассы"), null), .. cash.Select(c => new FilterChoice<int>(c.Name, c.Id))];
        CustomerChoices = [new(L.T("Все покупатели"), null),
            .. customers.Where(c => c.IsActive || c.Id == current.CustomerId).Select(c => new FilterChoice<int>(c.Name, c.Id))];
        Load(current);
    }

    public string Title => L.T("Фильтр");
    public double DialogWidth => 440;
    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<FilterChoice<int>> WarehouseChoices { get; }
    public IReadOnlyList<FilterChoice<int>> CashChoices { get; }
    public IReadOnlyList<FilterChoice<int>> CustomerChoices { get; }

    [ObservableProperty] private FilterChoice<int>? _warehouse;
    [ObservableProperty] private FilterChoice<int>? _cash;
    [ObservableProperty] private FilterChoice<int>? _customer;
    public SalesFilter? Result { get; private set; }

    private void Load(SalesFilter f)
    {
        Warehouse = WarehouseChoices.FirstOrDefault(c => c.Value == f.WarehouseId) ?? WarehouseChoices[0];
        Cash = CashChoices.FirstOrDefault(c => c.Value == f.CashRegisterId) ?? CashChoices[0];
        Customer = CustomerChoices.FirstOrDefault(c => c.Value == f.CustomerId) ?? CustomerChoices[0];
    }

    [RelayCommand]
    private void Reset() => Load(new SalesFilter());

    [RelayCommand]
    private void Apply()
    {
        Result = new SalesFilter(Warehouse?.Value, Cash?.Value, Customer?.Value);
        CloseRequested?.Invoke(this, true);
    }
}

/// <summary>Модель окна «Возврат по продаже».</summary>
public partial class SaleReturnViewModel : ObservableObject, ICloseRequest
{
    private readonly Sale _sale;
    private readonly SalesService _sales;
    private readonly IDialogService _dialogs;

    public SaleReturnViewModel(Sale sale, SalesService sales, IDialogService dialogs, IEnumerable<CashRegister> cashRegisters)
    {
        _sale = sale;
        _sales = sales;
        _dialogs = dialogs;
        CashRegisters = cashRegisters.ToList();
        CashRegister = CashRegisters.FirstOrDefault(c => c.Id == sale.CashRegisterId) ?? CashRegisters.FirstOrDefault();
        foreach (var i in sale.Items.Where(i => i.Quantity - i.ReturnedQuantity > 0))
        {
            var line = new ReturnLineViewModel(i);
            line.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(Total)); OnPropertyChanged(nameof(Refund)); OnPropertyChanged(nameof(Hint)); };
            Lines.Add(line);
        }
        _ = LoadDebtLimitAsync();
    }

    /// <summary>Сколько из возврата можно зачесть в долг покупателя по этой продаже (продажа в долг).</summary>
    private decimal _debtLimit;

    private async Task LoadDebtLimitAsync()
    {
        try { _debtLimit = await _sales.GetReturnDebtOffsetLimitAsync(_sale.Id); }
        catch (Exception ex) { ErrorLog.Write(ex, GetType().Name); _debtLimit = 0; }
        OnPropertyChanged(nameof(Refund));
        OnPropertyChanged(nameof(Hint));
    }

    /// <summary>Часть возврата, которая уменьшит долг покупателя (деньги не выдаются).</summary>
    public decimal DebtOffset => Math.Min(Total, _debtLimit);
    /// <summary>Сколько выдать покупателю из кассы.</summary>
    public decimal Refund => Total - DebtOffset;

    public string Title => L.F("Возврат по продаже {0}", _sale.Number);
    public string Subtitle => L.F("от {0:dd.MM.yyyy HH:mm} · {1} · {2}", _sale.Date, _sale.Customer?.Name ?? L.T("Розничный покупатель"),
        Core.Common.Money.Format(_sale.Total));
    public double DialogWidth => 640;
    public string CashLabel => L.T("Касса выдачи денег");
    public string? Hint => Total <= 0 || DebtOffset <= 0 ? null
        : Refund <= 0 ? L.F("Долг покупателя уменьшится на {0}. Деньги из кассы не выдаются.", Core.Common.Money.Format(Total))
        : L.F("Долг покупателя уменьшится на {0}, из кассы будет выдано {1}.", Core.Common.Money.Format(DebtOffset), Core.Common.Money.Format(Refund));
    public event EventHandler<bool>? CloseRequested;

    public List<ReturnLineViewModel> Lines { get; } = new();
    public IReadOnlyList<CashRegister> CashRegisters { get; }
    [ObservableProperty] private CashRegister? _cashRegister;
    [ObservableProperty] private string? _reason;
    public decimal Total => Lines.Sum(l => l.Amount);
    public bool Posted { get; private set; }

    [RelayCommand]
    private void ReturnAll()
    {
        foreach (var l in Lines) l.ReturnQuantity = l.Available;
    }

    [RelayCommand]
    private async Task PostAsync()
    {
        if (CashRegister is null) { _dialogs.Error(L.T("Выберите кассу.")); return; }
        var lines = Lines.Where(l => l.ReturnQuantity > 0).Select(l => new ReturnLine(l.SaleItemId, l.ReturnQuantity)).ToList();
        if (lines.Count == 0) { _dialogs.Error(L.T("Укажите количество к возврату.")); return; }
        var question = Hint is { } hint
            ? L.F("Оформить возврат по {0} на сумму {1}?", _sale.Number, Core.Common.Money.Format(Total)) + "\n" + hint
            : L.F("Оформить возврат по {0} на сумму {1}?\nДеньги будут выданы из кассы «{2}».",
                _sale.Number, Core.Common.Money.Format(Total), CashRegister.Name);
        if (!_dialogs.Confirm(question)) return;
        try
        {
            await _sales.CreateReturnAsync(new SaleReturnRequest(_sale.Id, CashRegister.Id, lines, Reason));
        }
        catch (Exception ex) { ErrorLog.Write(ex, GetType().Name); _dialogs.Error(L.Message(ex)); return; }
        Posted = true;
        if (FiscalPrinter.Enabled)
        {
            string? fiscalError;
            try { fiscalError = await FiscalPrinter.ReturnAsync(_sale, lines, CashRegister); }
            catch (Exception ex) { ErrorLog.Write(ex, GetType().Name); fiscalError = ex.Message; }
            if (fiscalError is not null)
                _dialogs.Error(L.F("Возврат оформлен, но чек возврата на ККТ не пробит:\n{0}", fiscalError));
        }
        CloseRequested?.Invoke(this, true);
    }
}
