using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.Views;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Закуп: оформление (и изменение проведённых), журнал закупок, расчёты с поставщиками.</summary>
public partial class PurchasesViewModel(PurchaseService purchases, FinanceService finance, CatalogService catalog,
    IDialogService dialogs, ICatalogDialogs catalogDialogs)
    : DocumentEditorBase(dialogs, catalogDialogs)
{
    public override string Title => L.T("Закупки");

    public ObservableCollection<Counterparty> Suppliers { get; } = new();
    public ObservableCollection<Warehouse> Warehouses { get; } = new();
    public ObservableCollection<CashRegister> CashRegisters { get; } = new();

    /// <summary>Открытая вкладка: 0 — оформление, 1 — журнал, 2 — поставщики и долги.</summary>
    // Без права оформления открываем сразу историю (вкладка оформления скрыта)
    [ObservableProperty] private int _selectedTab = Core.Services.Access.Can(Core.Domain.Perm.PurchasesCreate) || Core.Services.Access.Can(Core.Domain.Perm.PurchasesEdit) ? 0 : 1;

    protected override decimal DefaultPrice(Product product) => product.PurchasePrice;

    private bool _loaded;

    public override Task LoadAsync() => RunAsync(async () =>
    {
        SetProducts(await catalog.GetAllAsync<Product>(activeOnly: true));
        Fill(Suppliers, await catalog.GetAllAsync<Counterparty>(true));
        Fill(Warehouses, await catalog.GetAllAsync<Warehouse>(true));
        Fill(CashRegisters, await catalog.GetAllAsync<CashRegister>(true));
        Warehouse ??= Warehouses.FirstOrDefault();
        CashRegister ??= CashRegisters.FirstOrDefault();
        DebtCashRegister ??= CashRegisters.FirstOrDefault();
        await LoadHistoryCore();
        await LoadReturnsCore();
        await LoadDebtsCore();
        _loaded = true;
    });

    // ══ Оформление закупки ══

    [ObservableProperty] private Counterparty? _supplier;
    [ObservableProperty] private Warehouse? _warehouse;
    [ObservableProperty] private CashRegister? _cashRegister;
    [ObservableProperty] private string? _comment;
    [ObservableProperty] private DateTime _documentDate = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaymentNow), nameof(DebtAfter), nameof(Overpaid))]
    private decimal _paidAmount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaymentNow), nameof(DebtAfter), nameof(Overpaid))]
    private bool _payInFull = true;

    /// <summary>Закупка, которую сейчас изменяют (null — оформляется новая).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(EditorTabTitle), nameof(AlreadyPaid), nameof(PayableNow),
        nameof(PaymentNow), nameof(DebtAfter), nameof(Overpaid), nameof(PayLabel), nameof(PostLabel))]
    private Purchase? _editingPurchase;

    public bool IsEditing => EditingPurchase is not null;
    public string EditorTabTitle => EditingPurchase is { } p ? L.F("Изменение {0}", p.Number) : L.T("Приёмка товаров");
    public string PostLabel => IsEditing ? L.T("Сохранить изменения") : L.T("Провести закупку");
    public string PayLabel => IsEditing ? L.T("Доплатить полностью") : L.T("Оплатить полностью");

    /// <summary>Уже оплачено по изменяемой закупке (эти оплаты сохраняются).</summary>
    public decimal AlreadyPaid => EditingPurchase?.PaidAmount ?? 0;
    public decimal PayableNow => Math.Max(0, Total - AlreadyPaid);
    public decimal PaymentNow => PayInFull ? PayableNow : Money.Round(PaidAmount);
    public decimal DebtAfter => Math.Max(0, PayableNow - PaymentNow);
    public bool Overpaid => !PayInFull && PaidAmount > PayableNow;

    // PayInFull — сумма оплаты следует за суммой закупки, пока пользователь не ввёл её вручную.
    private bool _syncingPaid;
    partial void OnPaidAmountChanged(decimal value) { if (!_syncingPaid) PayInFull = false; }
    partial void OnPayInFullChanged(bool value) { if (value) SyncPaidAmount(); }
    private void SyncPaidAmount()
    {
        _syncingPaid = true;
        try { PaidAmount = PayableNow; } finally { _syncingPaid = false; }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(Total)) return; // итог считается в базовом классе
        if (PayInFull) SyncPaidAmount();
        OnPropertyChanged(nameof(PayableNow));
        OnPropertyChanged(nameof(PaymentNow));
        OnPropertyChanged(nameof(DebtAfter));
        OnPropertyChanged(nameof(Overpaid));
    }

    [RelayCommand]
    private async Task AddSupplierAsync()
    {
        if (await CatalogDialogs.EditAsync(Suppliers) is { Saved: { } s }) Supplier = s;
    }

    [RelayCommand]
    private async Task EditSupplierAsync()
    {
        if (Supplier is null) { Dialogs.Error(L.T("Выберите поставщика.")); return; }
        if (await CatalogDialogs.EditAsync(Suppliers, Supplier) is { } r) Supplier = r.Saved;
    }

    [RelayCommand]
    private async Task PostPurchaseAsync()
    {
        if (Supplier is null) { Dialogs.Error(L.T("Выберите поставщика.")); return; }
        if (Warehouse is null) { Dialogs.Error(L.T("Выберите склад.")); return; }
        if (Lines.Count == 0) { Dialogs.Error(L.T("Добавьте товары.")); return; }
        if (Lines.Any(l => l.Quantity <= 0)) { Dialogs.Error(L.T("Количество в каждой строке должно быть больше нуля.")); return; }
        if (Total < AlreadyPaid)
        {
            Dialogs.Error(L.F("Сумма закупки ({0}) меньше уже оплаченного ({1}).", Money.Format(Total), Money.Format(AlreadyPaid)));
            return;
        }
        if (Overpaid) { Dialogs.Error(L.F("Оплата больше суммы к оплате ({0}).", Money.Format(PayableNow))); return; }
        var paid = PaymentNow;
        if (paid > 0 && CashRegister is null) { Dialogs.Error(L.T("Выберите кассу для оплаты.")); return; }

        var editing = EditingPurchase;
        // Дата: при изменении сохраняем время документа, у новой — текущее время выбранного дня
        var date = editing is not null && editing.Date.Date == DocumentDate.Date ? editing.Date : DocumentDate.Date + DateTime.Now.TimeOfDay;
        var request = new PurchaseRequest(Supplier.Id, Warehouse.Id,
            Lines.Select(l => new DocumentLine(l.Product.Id, l.Quantity, l.Price)).ToList(),
            paid, paid > 0 ? CashRegister?.Id : null, Comment, date);

        Purchase? doc = null;
        var ok = await RunAsync(async () =>
        {
            doc = editing is null ? await purchases.CreatePurchaseAsync(request) : await purchases.UpdatePurchaseAsync(editing.Id, request);
        });
        if (!ok || doc is null) return;

        // Изменённые в строках цены продажи (розница и опт) — в карточки товаров
        var salePrices = Lines.Where(l => l.SalePrice != l.Product.SalePrice || l.WholesalePrice != l.Product.WholesalePrice)
            .GroupBy(l => l.Product.Id).ToDictionary(g => g.Key, g => (g.Last().SalePrice, g.Last().WholesalePrice));
        if (salePrices.Count > 0) await RunAsync(() => catalog.SetSalePricesAsync(salePrices));

        LastResult = editing is not null
            ? L.F("Закупка {0} изменена: {1}, долг {2}", doc.Number, Money.Format(doc.Total), Money.Format(doc.Debt))
            : doc.Debt > 0
                ? L.F("Закупка {0} проведена: {1}, оплачено {2}, долг {3}", doc.Number, Money.Format(doc.Total), Money.Format(doc.PaidAmount), Money.Format(doc.Debt))
                : L.F("Закупка {0} проведена и оплачена: {1}", doc.Number, Money.Format(doc.Total));
        NewPurchase();
        await RunAsync(async () =>
        {
            SetProducts(await catalog.GetAllAsync<Product>(activeOnly: true)); // обновлены закупочные цены
            await LoadHistoryCore();
            await LoadDebtsCore();
        });
        if (editing is not null) SelectedTab = 1;
    }

    /// <summary>Итог последнего проведения — плашкой над документом вместо окна-сообщения.</summary>
    [ObservableProperty] private string? _lastResult;

    [RelayCommand]
    private void DismissResult() => LastResult = null;

    [RelayCommand]
    private void NewPurchase()
    {
        ClearLines();
        EditingPurchase = null;
        PayInFull = true;
        SyncPaidAmount();
        Comment = null;
        DocumentDate = DateTime.Today;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        NewPurchase();
        SelectedTab = 1;
    }

    /// <summary>Загрузить закупку в редактор. asEdit — изменение этой закупки, иначе — новая по образцу.</summary>
    private async Task OpenInEditorAsync(Purchase? row, bool asEdit)
    {
        if (row is null) return;
        Purchase? p = null;
        if (!await RunAsync(async () => p = await purchases.GetPurchaseAsync(row.Id)) || p is null) return;
        if (Lines.Count > 0 && EditingPurchase is null &&
            !Dialogs.Confirm(L.T("В текущей закупке есть товары. Заменить их содержимым выбранной закупки?"))) return;

        NewPurchase();
        var missing = 0;
        foreach (var i in p.Items)
        {
            var product = Products.FirstOrDefault(x => x.Id == i.ProductId);
            if (product is null && asEdit && i.Product is not null) { product = i.Product; Products.Add(product); }
            if (product is null) { missing++; continue; }
            AddOrMerge(product, i.Quantity, i.Price);
        }
        Supplier = Suppliers.FirstOrDefault(s => s.Id == p.SupplierId);
        if (Supplier is null && asEdit && p.Supplier is not null) { Suppliers.Add(p.Supplier); Supplier = p.Supplier; }
        Warehouse = Warehouses.FirstOrDefault(w => w.Id == p.WarehouseId) ?? Warehouse;
        Comment = p.Comment;
        if (asEdit)
        {
            DocumentDate = p.Date.Date;
            EditingPurchase = p;
            PayInFull = false; // по умолчанию без доплаты
            PaidAmount = 0;
        }
        SelectedTab = 0;
        if (missing > 0)
            Dialogs.Error(L.F("Товаров из закупки больше нет в справочнике: {0}. Они не добавлены.", missing));
    }

    // ══ Журнал закупок ══

    public ObservableCollection<Purchase> History { get; } = new();

    [ObservableProperty] private DateTime _from = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime _to = DateTime.Today;
    partial void OnFromChanged(DateTime value) => ReloadFromFirstPage();
    partial void OnToChanged(DateTime value) => ReloadFromFirstPage();

    [ObservableProperty] private decimal _periodTotal;
    [ObservableProperty] private decimal _periodPaid;
    public decimal PeriodDebt => PeriodTotal - PeriodPaid;
    partial void OnPeriodTotalChanged(decimal value) => OnPropertyChanged(nameof(PeriodDebt));
    partial void OnPeriodPaidChanged(decimal value) => OnPropertyChanged(nameof(PeriodDebt));

    [ObservableProperty] private string? _historySearch;
    private CancellationTokenSource? _searchDelay;

    partial void OnHistorySearchChanged(string? value)
    {
        _searchDelay?.Cancel();
        var cts = _searchDelay = new CancellationTokenSource();
        _ = Task.Delay(300, cts.Token).ContinueWith(_ => ReloadFromFirstPage(), cts.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public PurchasesFilter Filter { get; private set; } = new();
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
        var f = new PurchaseFilter(From, To, Filter.SupplierId, Filter.WarehouseId, Filter.OnlyWithDebt, HistorySearch);
        var result = await purchases.GetPurchasesPageAsync(f, (Page - 1) * PageSize, PageSize);
        if (version != _loadVersion) return; // ответ на устаревший запрос

        if (result.Items.Count == 0 && result.TotalCount > 0 && Page > 1)
        {
            Page = Math.Max(1, (result.TotalCount + PageSize - 1) / PageSize);
            await LoadHistoryCore();
            return;
        }

        var selectedId = SelectedPurchase?.Id;
        Fill(History, result.Items);
        SelectedPurchase = History.FirstOrDefault(p => p.Id == selectedId);
        TotalCount = result.TotalCount;
        PeriodTotal = result.Total;
        PeriodPaid = result.Paid;
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
        var vm = new PurchasesFilterViewModel(Filter, Warehouses, await catalog.GetAllAsync<Counterparty>());
        ModalHost.ShowDialog(new FilterWindow { DataContext = vm });
        if (vm.Result is not { } applied) return;
        Filter = applied;
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(ActiveFilterCount));
        Page = 1;
        await RunAsync(LoadHistoryCore);
    }

    // ── Карточка выбранной закупки: состав, оплаты, погашение долга ──

    public ObservableCollection<PurchaseItem> SelectedItems { get; } = new();
    public ObservableCollection<CashTransaction> SelectedPayments { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedHasDebt))]
    private Purchase? _selectedPurchase;

    public bool HasSelection => SelectedPurchase is not null;
    public bool HasPayments => SelectedPayments.Count > 0;
    public bool SelectedHasDebt => SelectedPurchase is { Debt: > 0 };

    private int _detailsVersion;

    partial void OnSelectedPurchaseChanged(Purchase? value) => _ = LoadDetailsAsync(value);

    private async Task LoadDetailsAsync(Purchase? value)
    {
        var version = ++_detailsVersion;
        if (value is null) { SelectedItems.Clear(); SelectedPayments.Clear(); return; }
        try
        {
            var full = await purchases.GetPurchaseAsync(value.Id);
            var payments = await purchases.GetPaymentsAsync(value.Id);
            if (version != _detailsVersion) return;
            Fill(SelectedItems, full?.Items ?? []);
            Fill(SelectedPayments, payments);
            OnPropertyChanged(nameof(HasPayments));
        }
        catch (Exception ex) { ErrorLog.Write(ex, GetType().Name); Dialogs.Error(L.Message(ex)); }
    }

    /// <summary>Оплата долга выбранной закупки — в модальном окне.</summary>
    [RelayCommand]
    private Task PayDebtAsync() => PayPurchaseAsync(SelectedPurchase);

    /// <summary>Двойной щелчок по строке журнала — карточка закупки в модальном окне.</summary>
    [RelayCommand]
    private void OpenPurchaseDetails(Purchase? p)
    {
        if (p is null) return;
        SelectedPurchase = p;
        ModalHost.ShowDialog(new PurchaseDetailsWindow { DataContext = this });
    }

    // ── Меню строки журнала: изменить, по образцу, оплатить, удалить ──

    [RelayCommand]
    private Task EditPurchaseAsync(Purchase? p) => OpenInEditorAsync(p, asEdit: true);

    [RelayCommand]
    private Task CopyPurchaseAsync(Purchase? p) => OpenInEditorAsync(p, asEdit: false);

    [RelayCommand]
    private async Task PayPurchaseAsync(Purchase? p)
    {
        if (p is null) return;
        if (p.Debt <= 0) { Dialogs.Info(L.F("Закупка {0} оплачена полностью.", p.Number)); return; }
        if (CashRegisters.Count == 0) { Dialogs.Error(L.T("Сначала добавьте кассу в разделе «Справочники».")); return; }
        SelectedPurchase = p;
        var vm = new PurchasePaymentViewModel(p, purchases, finance, catalogDialogs, CashRegisters);
        ModalHost.ShowDialog(new OperationWindow { DataContext = vm });
        if (!vm.Posted) return;
        await RunAsync(async () => { await LoadHistoryCore(); await LoadDebtsCore(); });
        // Обновить карточку, даже если строка осталась той же
        await LoadDetailsAsync(SelectedPurchase);
    }

    /// <summary>Возврат товара поставщику по закупке.</summary>
    [RelayCommand]
    private async Task ReturnPurchaseAsync(Purchase? p)
    {
        p ??= SelectedPurchase;
        if (p is null) return;
        Purchase? full = null;
        if (!await RunAsync(async () => full = await purchases.GetPurchaseAsync(p.Id)) || full is null) return;
        if (full.Items.All(i => i.Quantity - i.ReturnedQuantity <= 0)) { Dialogs.Info(L.F("По закупке {0} весь товар уже возвращён.", full.Number)); return; }
        SelectedPurchase = p;
        var vm = new PurchaseReturnViewModel(full, purchases, Dialogs, CashRegisters);
        ModalHost.ShowDialog(new SaleReturnWindow { DataContext = vm });
        if (!vm.Posted) return;
        await RunAsync(async () => { await LoadHistoryCore(); await LoadReturnsCore(); await LoadDebtsCore(); });
        await LoadDetailsAsync(SelectedPurchase);
    }

    // ══ Журнал возвратов поставщикам ══

    public ObservableCollection<PurchaseReturn> Returns { get; } = new();

    [ObservableProperty] private DateTime _returnsFrom = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime _returnsTo = DateTime.Today;
    [ObservableProperty] private decimal _returnsTotal;
    [ObservableProperty] private decimal _returnsRefund;
    partial void OnReturnsFromChanged(DateTime value) { if (_loaded) _ = RunAsync(LoadReturnsCore); }
    partial void OnReturnsToChanged(DateTime value) { if (_loaded) _ = RunAsync(LoadReturnsCore); }

    [RelayCommand]
    private Task LoadReturnsAsync() => RunAsync(LoadReturnsCore);

    private async Task LoadReturnsCore()
    {
        var list = await purchases.GetReturnsAsync(ReturnsFrom, ReturnsTo);
        Fill(Returns, list);
        ReturnsTotal = list.Sum(r => r.Total);
        ReturnsRefund = list.Sum(r => r.RefundAmount);
    }

    [RelayCommand]
    private async Task DeletePurchaseAsync(Purchase? p)
    {
        if (p is null) return;
        var message = p.PaidAmount > 0
            ? L.F("Удалить закупку {0} на сумму {1}?\nТовар будет списан со склада, оплата {2} вернётся в кассу.", p.Number, Money.Format(p.Total), Money.Format(p.PaidAmount))
            : L.F("Удалить закупку {0} на сумму {1}?\nТовар будет списан со склада.", p.Number, Money.Format(p.Total));
        if (!Dialogs.Confirm(message)) return;
        if (!await RunAsync(() => purchases.DeletePurchaseAsync(p.Id))) return;
        if (EditingPurchase?.Id == p.Id) NewPurchase();
        if (SelectedPurchase?.Id == p.Id) SelectedPurchase = null;
        await RunAsync(async () => { await LoadHistoryCore(); await LoadDebtsCore(); });
    }

    // ══ Поставщики и долги ══

    public ObservableCollection<SupplierDebtRow> SupplierDebts { get; } = new();
    [ObservableProperty] private decimal _totalDebt;
    [ObservableProperty] private bool _onlyDebtors = true;
    partial void OnOnlyDebtorsChanged(bool value) { if (_loaded) _ = RunAsync(LoadDebtsCore); }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDebtSelection))]
    private SupplierDebtRow? _selectedDebt;
    public bool HasDebtSelection => SelectedDebt is { Debt: > 0 };

    [ObservableProperty] private decimal _debtPayAmount;
    [ObservableProperty] private CashRegister? _debtCashRegister;
    [ObservableProperty] private string? _debtComment;

    partial void OnSelectedDebtChanged(SupplierDebtRow? value) => DebtPayAmount = value?.Debt > 0 ? value.Debt : 0;

    [RelayCommand]
    private Task LoadDebtsAsync() => RunAsync(LoadDebtsCore);

    private async Task LoadDebtsCore()
    {
        var rows = await purchases.GetSupplierDebtsAsync();
        TotalDebt = rows.Where(r => r.Debt > 0).Sum(r => r.Debt);
        var selectedId = SelectedDebt?.SupplierId;
        Fill(SupplierDebts, OnlyDebtors ? rows.Where(r => r.Debt > 0) : rows);
        SelectedDebt = SupplierDebts.FirstOrDefault(r => r.SupplierId == selectedId);
    }

    /// <summary>Оплата поставщику: гасит его закупки начиная с самых старых.</summary>
    [RelayCommand]
    private async Task PaySupplierAsync()
    {
        if (SelectedDebt is not { } row) { Dialogs.Error(L.T("Выберите поставщика.")); return; }
        if (DebtCashRegister is null) { Dialogs.Error(L.T("Выберите кассу.")); return; }
        if (DebtPayAmount <= 0) { Dialogs.Error(L.T("Сумма оплаты должна быть больше нуля.")); return; }
        if (DebtPayAmount > row.Debt)
        {
            Dialogs.Error(L.F("Сумма больше долга поставщику ({0}). Аванс оформляется в разделе «Финансы».", Money.Format(row.Debt)));
            return;
        }
        if (!Dialogs.Confirm(L.F("Оплатить поставщику «{0}» {1} из кассы «{2}»?\nДолг гасится по закупкам начиная с самых старых.",
                row.Supplier, Money.Format(DebtPayAmount), DebtCashRegister.Name))) return;
        var request = new CounterpartyPaymentRequest(CashDirection.Expense, DebtCashRegister.Id, row.SupplierId, DebtPayAmount, "Оплата поставщику", DebtComment);
        if (!await RunAsync(() => finance.CounterpartyPaymentAsync(request))) return;
        DebtComment = null;
        await RunAsync(async () => { await LoadDebtsCore(); await LoadHistoryCore(); });
    }

    /// <summary>Открыть в журнале все неоплаченные закупки поставщика.</summary>
    [RelayCommand]
    private async Task ShowSupplierPurchasesAsync(SupplierDebtRow? row)
    {
        row ??= SelectedDebt;
        if (row is null) return;
        Filter = new PurchasesFilter(row.SupplierId, null, OnlyWithDebt: row.Debt > 0);
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(ActiveFilterCount));
        _loaded = false; // одна перезагрузка вместо трёх
        From = DateTime.Today.AddYears(-5);
        To = DateTime.Today;
        HistorySearch = null;
        _loaded = true;
        Page = 1;
        SelectedTab = 1;
        await RunAsync(LoadHistoryCore);
    }
}

/// <summary>Условия фильтра журнала закупок (кроме периода и поиска).</summary>
public sealed record PurchasesFilter(int? SupplierId = null, int? WarehouseId = null, bool OnlyWithDebt = false)
{
    public int ActiveCount => (SupplierId is null ? 0 : 1) + (WarehouseId is null ? 0 : 1) + (OnlyWithDebt ? 1 : 0);
}

/// <summary>Модель окна «Фильтр» журнала закупок.</summary>
public partial class PurchasesFilterViewModel : ObservableObject, ICloseRequest
{
    public PurchasesFilterViewModel(PurchasesFilter current, IEnumerable<Warehouse> warehouses, IEnumerable<Counterparty> suppliers)
    {
        SupplierChoices = [new(L.T("Все поставщики"), null),
            .. suppliers.Where(c => c.IsActive || c.Id == current.SupplierId).Select(c => new FilterChoice<int>(c.Name, c.Id))];
        WarehouseChoices = [new(L.T("Все склады"), null), .. warehouses.Select(w => new FilterChoice<int>(w.Name, w.Id))];
        Load(current);
    }

    public string Title => L.T("Фильтр");
    public double DialogWidth => 440;
    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<FilterChoice<int>> SupplierChoices { get; }
    public IReadOnlyList<FilterChoice<int>> WarehouseChoices { get; }

    [ObservableProperty] private FilterChoice<int>? _supplier;
    [ObservableProperty] private FilterChoice<int>? _warehouse;
    [ObservableProperty] private bool _onlyWithDebt;
    public PurchasesFilter? Result { get; private set; }

    private void Load(PurchasesFilter f)
    {
        Supplier = SupplierChoices.FirstOrDefault(c => c.Value == f.SupplierId) ?? SupplierChoices[0];
        Warehouse = WarehouseChoices.FirstOrDefault(c => c.Value == f.WarehouseId) ?? WarehouseChoices[0];
        OnlyWithDebt = f.OnlyWithDebt;
    }

    [RelayCommand]
    private void Reset() => Load(new PurchasesFilter());

    [RelayCommand]
    private void Apply()
    {
        Result = new PurchasesFilter(Supplier?.Value, Warehouse?.Value, OnlyWithDebt);
        CloseRequested?.Invoke(this, true);
    }
}

/// <summary>Строка окна возврата поставщику.</summary>
public partial class PurchaseReturnLineViewModel(PurchaseItem item) : ObservableObject
{
    public int PurchaseItemId => item.Id;
    public string Product => item.Product?.Name ?? "";
    public decimal Available => item.Quantity - item.ReturnedQuantity;
    public decimal Price => item.Price;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Amount))]
    private decimal _returnQuantity;

    public decimal Amount => Money.Round(ReturnQuantity * Price);
}

/// <summary>Модель окна возврата товара поставщику (Views/SaleReturnWindow).</summary>
public partial class PurchaseReturnViewModel : ObservableObject, ICloseRequest
{
    private readonly Purchase _purchase;
    private readonly PurchaseService _purchases;
    private readonly IDialogService _dialogs;

    public PurchaseReturnViewModel(Purchase purchase, PurchaseService purchases, IDialogService dialogs, IEnumerable<CashRegister> cashRegisters)
    {
        _purchase = purchase;
        _purchases = purchases;
        _dialogs = dialogs;
        CashRegisters = cashRegisters.ToList();
        CashRegister = CashRegisters.FirstOrDefault();
        foreach (var i in purchase.Items.Where(i => i.Quantity - i.ReturnedQuantity > 0))
        {
            var line = new PurchaseReturnLineViewModel(i);
            line.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(Total)); OnPropertyChanged(nameof(Refund)); OnPropertyChanged(nameof(Hint)); };
            Lines.Add(line);
        }
    }

    public string Title => L.F("Возврат поставщику по {0}", _purchase.Number);
    public string Subtitle => L.F("от {0:dd.MM.yyyy HH:mm} · {1} · {2}", _purchase.Date, _purchase.Supplier?.Name ?? "",
        Money.Format(_purchase.Total));
    public double DialogWidth => 640;
    public string CashLabel => L.T("Касса для возврата денег");
    public event EventHandler<bool>? CloseRequested;

    public List<PurchaseReturnLineViewModel> Lines { get; } = new();
    public IReadOnlyList<CashRegister> CashRegisters { get; }
    [ObservableProperty] private CashRegister? _cashRegister;
    [ObservableProperty] private string? _reason;
    public decimal Total => Lines.Sum(l => l.Amount);
    /// <summary>Сколько поставщик вернёт деньгами: оплачено сверх новой суммы закупки.</summary>
    public decimal Refund => Math.Max(0, Money.Round(_purchase.PaidAmount - (_purchase.Total - Total)));
    public decimal DebtOffset => Total - Refund;
    public bool Posted { get; private set; }

    public string? Hint => Total <= 0 ? null
        : Refund <= 0 ? L.F("Долг перед поставщиком уменьшится на {0}. Деньги из кассы не возвращаются.", Money.Format(Total))
        : DebtOffset > 0 ? L.F("Долг уменьшится на {0}, поставщик вернёт {1} в выбранную кассу.", Money.Format(DebtOffset), Money.Format(Refund))
        : L.F("Закупка оплачена — поставщик вернёт {0} в выбранную кассу.", Money.Format(Refund));

    [RelayCommand]
    private void ReturnAll()
    {
        foreach (var l in Lines) l.ReturnQuantity = l.Available;
    }

    [RelayCommand]
    private async Task PostAsync()
    {
        var lines = Lines.Where(l => l.ReturnQuantity > 0).Select(l => new PurchaseReturnLine(l.PurchaseItemId, l.ReturnQuantity)).ToList();
        if (lines.Count == 0) { _dialogs.Error(L.T("Укажите количество к возврату.")); return; }
        if (Refund > 0 && CashRegister is null) { _dialogs.Error(L.T("Выберите кассу.")); return; }
        if (!_dialogs.Confirm(L.F("Оформить возврат поставщику по {0} на сумму {1}?\nТовар будет списан со склада.", _purchase.Number, Money.Format(Total))
                + (Hint is { } h ? "\n" + h : ""))) return;
        try
        {
            await _purchases.CreateReturnAsync(new PurchaseReturnRequest(_purchase.Id, lines, Refund > 0 ? CashRegister?.Id : null, Reason));
        }
        catch (Exception ex) { ErrorLog.Write(ex, GetType().Name); _dialogs.Error(L.Message(ex)); return; }
        Posted = true;
        CloseRequested?.Invoke(this, true);
    }
}
