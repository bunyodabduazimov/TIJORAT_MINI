using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.Views;
using MiniTrade.Desktop.Infrastructure.Printing;
using System.Windows;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>
/// Кассы и журнал операций (постранично). Операции создаются из меню «Создать» в заголовке страницы
/// и оформляются в модальных окнах (Views/OperationWindow); условия журнала — в окне «Фильтр».
/// </summary>
public partial class FinanceViewModel(
    FinanceService finance, CatalogService catalog, SettingsService settings, IDialogService dialogs, ICatalogDialogs catalogDialogs, StatementService statements) : PageViewModel(dialogs)
{
    public override string Title => L.T("Финансы");

    public ObservableCollection<CashRegister> CashRegisters { get; } = new();
    public ObservableCollection<CashTransaction> Operations { get; } = new();

    [ObservableProperty] private decimal _totalCash;
    [ObservableProperty] private decimal _periodIncome;
    [ObservableProperty] private decimal _periodExpense;
    // ── Вкладки: 0 — платежи, 1 — кассы, 2 — взаиморасчёты, 3 — выписка ──
    [ObservableProperty] private int _selectedTab;

    // ── Кассы: остатки на конец выбранного дня ──
    public ObservableCollection<CashBalanceRow> CashBalances { get; } = new();
    [ObservableProperty] private DateTime _cashDate = DateTime.Today;
    [ObservableProperty] private decimal _cashDateTotal;
    partial void OnCashDateChanged(DateTime value) { if (_loaded) _ = RunAsync(LoadCashBalancesCore); }

    private async Task LoadCashBalancesCore()
    {
        var rows = await statements.GetCashBalancesAsync(CashDate);
        Fill(CashBalances, rows);
        CashDateTotal = rows.Sum(r => r.Balance);
    }

    // ── Взаиморасчёты: балансы контрагентов на конец дня (+ должен нам, − мы должны) ──
    public ObservableCollection<CounterpartyBalanceRow> Counterparties { get; } = new();
    public ObservableCollection<FilterChoice<int>> GroupChoices { get; } = new();
    private System.ComponentModel.ICollectionView? _counterpartiesView;
    public System.ComponentModel.ICollectionView CounterpartiesView => _counterpartiesView ??= CreateCounterpartiesView();
    private System.ComponentModel.ICollectionView CreateCounterpartiesView()
    {
        var v = System.Windows.Data.CollectionViewSource.GetDefaultView(Counterparties);
        v.Filter = o => o is CounterpartyBalanceRow c && MatchCounterparty(c);
        return v;
    }
    [ObservableProperty] private DateTime _debtDate = DateTime.Today;
    [ObservableProperty] private FilterChoice<int>? _debtGroup;
    [ObservableProperty] private string? _debtSearch;
    [ObservableProperty] private bool _onlyWithDebt = true;
    [ObservableProperty] private decimal _receivable;
    [ObservableProperty] private decimal _payable;
    partial void OnDebtDateChanged(DateTime value) { if (_loaded) _ = RunAsync(LoadCounterpartyBalancesCore); }
    partial void OnDebtGroupChanged(FilterChoice<int>? value) => RefreshCounterparties();
    partial void OnDebtSearchChanged(string? value) => RefreshCounterparties();
    partial void OnOnlyWithDebtChanged(bool value) => RefreshCounterparties();

    private bool MatchCounterparty(CounterpartyBalanceRow c)
    {
        if (OnlyWithDebt && c.Balance == 0) return false;
        if (DebtGroup?.Value is { } g && c.GroupId != g) return false;
        var q = DebtSearch?.Trim();
        return string.IsNullOrEmpty(q) || c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || (c.Phone?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    /// <summary>Фильтр списка и итоги по видимым строкам.</summary>
    private void RefreshCounterparties()
    {
        CounterpartiesView.Refresh();
        var visible = CounterpartiesView.Cast<CounterpartyBalanceRow>().ToList();
        Receivable = visible.Where(c => c.Balance > 0).Sum(c => c.Balance);
        Payable = -visible.Where(c => c.Balance < 0).Sum(c => c.Balance);
    }

    private async Task LoadCounterpartyBalancesCore()
    {
        Fill(Counterparties, await statements.GetCounterpartyBalancesAsync(DebtDate));
        RefreshCounterparties();
    }

    // ── Выписка: движение по кассе или контрагенту за период ──
    public IReadOnlyList<string> StatementKinds { get; } = [L.T("Касса"), L.T("Контрагент")];
    public ObservableCollection<Counterparty> AllCounterparties { get; } = new();
    public ObservableCollection<StatementRow> StatementRows { get; } = new();
    [ObservableProperty] private int _statementKind;
    [ObservableProperty] private CashRegister? _statementCash;
    [ObservableProperty] private Counterparty? _statementCounterparty;
    [ObservableProperty] private DateTime _statementFrom = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateTime _statementTo = DateTime.Today;
    [ObservableProperty] private Statement? _statement;
    partial void OnStatementKindChanged(int value) => ReloadStatement();
    partial void OnStatementCashChanged(CashRegister? value) => ReloadStatement();
    partial void OnStatementCounterpartyChanged(Counterparty? value) => ReloadStatement();
    partial void OnStatementFromChanged(DateTime value) => ReloadStatement();
    partial void OnStatementToChanged(DateTime value) => ReloadStatement();
    private void ReloadStatement() { if (_loaded) _ = RunAsync(LoadStatementCore); }

    private async Task LoadStatementCore()
    {
        Statement? s = StatementKind == 0
            ? StatementCash is { } c ? await statements.GetCashStatementAsync(c.Id, StatementFrom, StatementTo) : null
            : StatementCounterparty is { } p ? await statements.GetCounterpartyStatementAsync(p.Id, StatementFrom, StatementTo) : null;
        Statement = s;
        Fill(StatementRows, s?.Rows ?? []);
    }

    // ── Экспорт и печать: взаиморасчёты и выписка ──

    private string StatementSubject => StatementKind == 0 ? StatementCash?.Name ?? "" : StatementCounterparty?.Name ?? "";
    private string StatementTitle => StatementKind == 0 ? L.T("Выписка по кассе") : L.T("Акт сверки с контрагентом");
    private string StatementPeriod => L.F("за период {0:dd.MM.yyyy} — {1:dd.MM.yyyy}", StatementFrom, StatementTo);

    [RelayCommand]
    private void ExportDebts()
    {
        var rows = CounterpartiesView.Cast<CounterpartyBalanceRow>().ToList();
        var lines = rows.Select(r => CsvRow(r.Name, r.Group, r.Phone, r.Balance))
            .Prepend(L.T("Контрагент;Группа;Телефон;Баланс"))
            .Append(CsvRow(L.T("Нам должны"), null, null, Receivable))
            .Append(CsvRow(L.T("Мы должны"), null, null, -Payable));
        SaveExcel($"{L.T("Расчёты с контрагентами")}_{DebtDate:yyyyMMdd}.xlsx", lines);
    }

    [RelayCommand]
    private async Task PrintDebtsAsync()
    {
        var rows = CounterpartiesView.Cast<CounterpartyBalanceRow>().ToList();
        var n = 0;
        var doc = PrintDocuments.TableReport(L.T("Взаиморасчёты с контрагентами"),
            L.F("на {0:dd.MM.yyyy}", DebtDate) + (DebtGroup?.Value is null ? "" : " · " + DebtGroup.Name),
            await settings.GetCompanyAsync(),
            [("№", new(34), false), (L.T("Контрагент"), new(1, GridUnitType.Star), false), (L.T("Группа"), new(130), false),
             (L.T("Телефон"), new(120), false), (L.T("Баланс"), new(110), true)],
            rows.Select(r => new List<string> { (++n).ToString(), r.Name, r.Group ?? "", r.Phone ?? "", $"{r.Balance:N2}" }),
            [L.F("Нам должны: {0}", MiniTrade.Core.Common.Money.Format(Receivable)), L.F("Мы должны: {0}", MiniTrade.Core.Common.Money.Format(Payable))]);
        DocumentPrinter.Print(L.T("Расчёты с контрагентами"), doc);
    }

    [RelayCommand]
    private void ExportStatement()
    {
        if (Statement is not { } s) { Dialogs.Info(L.T("Выберите кассу или контрагента.")); return; }
        var lines = s.Rows.Select(r => CsvRow(r.Date.ToString("dd.MM.yyyy HH:mm"), r.Document, r.Operation, r.Comment, r.Income, r.Expense, r.Balance))
            .Prepend(CsvRow(L.T("Начальный остаток"), null, null, null, null, null, s.Opening))
            .Prepend(L.T("Дата;Документ;Операция;Комментарий;Приход;Расход;Остаток"))
            .Append(CsvRow(L.T("Итого"), null, null, null, s.Income, s.Expense, s.Closing));
        SaveExcel($"{L.T("Движение средств")}_{StatementSubject}_{StatementFrom:yyyyMMdd}-{StatementTo:yyyyMMdd}.xlsx", lines);
    }

    [RelayCommand]
    private async Task PrintStatementAsync()
    {
        if (Statement is not { } s) { Dialogs.Info(L.T("Выберите кассу или контрагента.")); return; }
        var M = (decimal v) => MiniTrade.Core.Common.Money.Format(v);
        var rows = s.Rows.Select(r => new List<string> { r.Date.ToString("dd.MM.yyyy HH:mm"), r.Document ?? "", r.Operation, r.Comment ?? "",
            r.Income == 0 ? "" : $"{r.Income:N2}", r.Expense == 0 ? "" : $"{r.Expense:N2}", $"{r.Balance:N2}" })
            .Prepend([L.F("{0:dd.MM.yyyy}", StatementFrom), "", L.T("Начальный остаток"), "", "", "", $"{s.Opening:N2}"]);
        var doc = PrintDocuments.TableReport(StatementTitle, $"{StatementSubject} · {StatementPeriod}", await settings.GetCompanyAsync(),
            [(L.T("Дата"), new(100), false), (L.T("Документ"), new(80), false), (L.T("Операция"), new(1, GridUnitType.Star), false),
             (L.T("Комментарий"), new(150), false), (L.T("Приход"), new(85), true), (L.T("Расход"), new(85), true), (L.T("Остаток"), new(90), true)],
            rows,
            [L.F("Приход: {0}   Расход: {1}", M(s.Income), M(s.Expense)), L.F("Конечный остаток: {0}", M(s.Closing))], landscape: true);
        DocumentPrinter.Print(StatementTitle, doc);
    }

    private void SaveExcel(string name, IEnumerable<string> lines)
    {
        foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        var path = Dialogs.SaveFile(name, ExcelExport.Filter);
        if (path is null) return;
        try
        {
            ExcelExport.Save(path, lines, System.IO.Path.GetFileNameWithoutExtension(name));
            Dialogs.Info(L.F("Файл сохранён: {0}", path));
        }
        catch (System.IO.IOException ex) { Dialogs.Error(ex.Message); }
    }

    private static string CsvRow(params object?[] values) => string.Join(';', values.Select(v => v switch
    {
        decimal d => d.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")),
        null => "",
        _ => '"' + v.ToString()!.Replace("\"", "\"\"") + '"'
    }));

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();



    // ── Период (на панели журнала): смена дат сразу обновляет журнал ──
    [ObservableProperty] private DateTime _from = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime _to = DateTime.Today;
    partial void OnFromChanged(DateTime value) => ReloadFromFirstPage();
    partial void OnToChanged(DateTime value) => ReloadFromFirstPage();

    // ── Быстрый поиск над журналом (статья, комментарий, № документа): с задержкой ввода ──
    [ObservableProperty] private string? _search;
    private CancellationTokenSource? _searchDelay;

    partial void OnSearchChanged(string? value)
    {
        _searchDelay?.Cancel();
        var cts = _searchDelay = new CancellationTokenSource();
        _ = Task.Delay(300, cts.Token).ContinueWith(_ => ReloadFromFirstPage(), cts.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ── Условия фильтра (окно «Фильтр») ──
    public FinanceFilter Filter { get; private set; } = new();
    public bool HasActiveFilter => Filter.ActiveCount > 0;
    public int ActiveFilterCount => Filter.ActiveCount;

    // ── Постраничный вывод ──
    public IReadOnlyList<int> PageSizes { get; } = [25, 50, 100, 200];
    [ObservableProperty] private int _pageSize = 50;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _totalCount;
    public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    public int FirstNumber => (Page - 1) * PageSize;
    public string PageInfo => TotalCount == 0
        ? L.T("Ничего не найдено")
        : L.F("Показано {0}–{1} из {2:N0}", FirstNumber + 1, FirstNumber + Operations.Count, TotalCount);
    public string PageLabel => L.F("{0} из {1}", Page, PageCount);

    partial void OnPageSizeChanged(int value) => ReloadFromFirstPage();

    private bool _loaded;
    private int _loadVersion;

    public override Task LoadAsync() => RunAsync(async () =>
    {
        var cash = await finance.GetCashRegistersAsync();
        Fill(CashRegisters, cash);
        TotalCash = cash.Sum(c => c.Balance);
        var cps = await catalog.GetAllAsync<Counterparty>();
        Fill(AllCounterparties, cps.Where(c => c.IsActive).OrderBy(c => c.Name));
        var groups = await catalog.GetAllAsync<CounterpartyGroup>();
        var selectedGroup = DebtGroup?.Value;
        Fill(GroupChoices, [new FilterChoice<int>(L.T("Все группы"), null), .. groups.Where(g => g.IsActive).OrderBy(g => g.Name).Select(g => new FilterChoice<int>(g.Name, g.Id))]);
        DebtGroup = GroupChoices.FirstOrDefault(g => g.Value == selectedGroup) ?? GroupChoices[0];
        StatementCash = CashRegisters.FirstOrDefault(c => c.Id == StatementCash?.Id) ?? CashRegisters.FirstOrDefault();
        StatementCounterparty = AllCounterparties.FirstOrDefault(c => c.Id == StatementCounterparty?.Id);
        await LoadCashBalancesCore();
        await LoadCounterpartyBalancesCore();
        await LoadStatementCore();
        await LoadOperationsCore();
        _loaded = true;
    });

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadOperationsCore);

    private void ReloadFromFirstPage()
    {
        if (!_loaded) return;
        Page = 1;
        _ = RunAsync(LoadOperationsCore);
    }

    private async Task LoadOperationsCore()
    {
        var version = ++_loadVersion;
        var f = new CashOperationFilter(From, To, Filter.CashRegisterId, Filter.Direction, Filter.CounterpartyId, Search, Filter.Kinds);
        var result = await finance.GetOperationsPageAsync(f, (Page - 1) * PageSize, PageSize);
        if (version != _loadVersion) return; // ответ на устаревший запрос

        // Страница опустела — показываем последнюю непустую
        if (result.Items.Count == 0 && result.TotalCount > 0 && Page > 1)
        {
            Page = Math.Max(1, (result.TotalCount + PageSize - 1) / PageSize);
            await LoadOperationsCore();
            return;
        }

        Fill(Operations, result.Items);
        TotalCount = result.TotalCount;
        PeriodIncome = result.Income;
        PeriodExpense = result.Expense;
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(FirstNumber));
        OnPropertyChanged(nameof(PageInfo));
        OnPropertyChanged(nameof(PageLabel));
        FirstPageCommand.NotifyCanExecuteChanged();
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
    }

    private bool CanGoBack => Page > 1;
    private bool CanGoForward => Page < PageCount;
    private Task GoToAsync(int page) { Page = page; return RunAsync(LoadOperationsCore); }

    [RelayCommand(CanExecute = nameof(CanGoBack))] private Task FirstPageAsync() => GoToAsync(1);
    [RelayCommand(CanExecute = nameof(CanGoBack))] private Task PrevPageAsync() => GoToAsync(Page - 1);
    [RelayCommand(CanExecute = nameof(CanGoForward))] private Task NextPageAsync() => GoToAsync(Page + 1);
    [RelayCommand(CanExecute = nameof(CanGoForward))] private Task LastPageAsync() => GoToAsync(PageCount);

    // ── Фильтр ──
    [RelayCommand]
    private async Task OpenFilterAsync()
    {
        var vm = new FinanceFilterViewModel(Filter, CashRegisters, await catalog.GetAllAsync<Counterparty>());
        ModalHost.ShowDialog(new FilterWindow { DataContext = vm });
        if (vm.Result is not { } applied) return;
        Filter = applied;
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(ActiveFilterCount));
        Page = 1;
        await RunAsync(LoadOperationsCore);
    }

    // ── Меню строки журнала: изменить, скопировать, печать, удалить ──

    /// <summary>Форма операции того же вида, что строка журнала; null — операция документа (продажа, закупка…).</summary>
    private OperationViewModel? FormFor(CashTransaction t) => t.Kind switch
    {
        CashOperationKind.Manual => new CashOperationViewModel(t.Direction, finance, catalogDialogs, CashRegisters),
        CashOperationKind.CounterpartyPayment => new CounterpartyPaymentViewModel(t.Direction, finance, catalog, catalogDialogs, CashRegisters),
        CashOperationKind.CounterpartyAccrual => new CounterpartyAccrualViewModel(finance, catalog, catalogDialogs, CashRegisters),
        CashOperationKind.SupplierPayment when t.DocumentId is null && t.CounterpartyId is not null
            => new CounterpartyPaymentViewModel(t.Direction, finance, catalog, catalogDialogs, CashRegisters),
        CashOperationKind.TransferIn or CashOperationKind.TransferOut when CashRegisters.Count > 1
            => new TransferViewModel(finance, catalogDialogs, CashRegisters),
        _ => null
    };

    // Права проверяются по подразделу операции: приход, расход, платёж, начисление, перевод
    private static bool CanModify(CashTransaction? t) => t is not null && FinanceService.CanModify(t) && FinanceService.CanDo(t, "edit");
    private static bool CanRemove(CashTransaction? t) => t is not null && FinanceService.CanModify(t) && FinanceService.CanDo(t, "delete");
    private bool CanCopy(CashTransaction? t) => t is not null && FormFor(t) is not null && FinanceService.CanDo(t, "create");

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task EditOperationAsync(CashTransaction? t) =>
        t is not null && FormFor(t) is { } vm ? OpenAsync(vm, t, asEdit: true) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private Task CopyOperationAsync(CashTransaction? t) =>
        t is not null && FormFor(t) is { } vm ? OpenAsync(vm, t) : Task.CompletedTask;

    [RelayCommand]
    private async Task PrintOperationAsync(CashTransaction? t)
    {
        if (t is null) return;
        await RunAsync(async () => CashOrderPrinter.Print(t, await settings.GetCompanyAsync()));
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task DeleteOperationAsync(CashTransaction? t)
    {
        if (t is null || !Dialogs.Confirm(L.F("Удалить операцию {0} на сумму {1}? Остаток кассы будет пересчитан.", t.DocumentNumber ?? "", MiniTrade.Core.Common.Money.Format(t.Amount)))) return;
        if (await RunAsync(() => finance.DeleteOperationAsync(t.Id))) await LoadAsync();
    }

    /// <summary>Двойной щелчок по строке: изменить, если можно, иначе — копия.</summary>
    [RelayCommand]
    private Task OpenOperationAsync(CashTransaction? t) =>
        CanModify(t) ? EditOperationAsync(t) : CanCopy(t) ? CopyOperationAsync(t) : Task.CompletedTask;

    // ── Операции в модальных окнах (меню «Создать») ──
    [RelayCommand] private Task IncomeAsync() => OpenAsync(new CashOperationViewModel(CashDirection.Income, finance, catalogDialogs, CashRegisters));
    [RelayCommand] private Task ExpenseAsync() => OpenAsync(new CashOperationViewModel(CashDirection.Expense, finance, catalogDialogs, CashRegisters));

    [RelayCommand]
    private Task TransferAsync()
    {
        if (CashRegisters.Count < 2) { Dialogs.Info(L.T("Для перемещения нужно минимум две кассы (Справочники → Кассы).")); return Task.CompletedTask; }
        return OpenAsync(new TransferViewModel(finance, catalogDialogs, CashRegisters));
    }

    [RelayCommand] private Task PayToAsync() => OpenAsync(new CounterpartyPaymentViewModel(CashDirection.Expense, finance, catalog, catalogDialogs, CashRegisters));
    [RelayCommand] private Task ReceiveFromAsync() => OpenAsync(new CounterpartyPaymentViewModel(CashDirection.Income, finance, catalog, catalogDialogs, CashRegisters));
    [RelayCommand] private Task AccrueAsync() => OpenAsync(new CounterpartyAccrualViewModel(finance, catalog, catalogDialogs, CashRegisters));

    private async Task OpenAsync(OperationViewModel vm, CashTransaction? source = null, bool asEdit = false)
    {
        if (CashRegisters.Count == 0) { Dialogs.Error(L.T("Сначала добавьте кассу в разделе «Справочники».")); return; }
        // Касса из фильтра журнала — разумный выбор по умолчанию
        if (Filter.CashRegisterId is { } id) vm.CashRegister = vm.CashRegisters.FirstOrDefault(c => c.Id == id) ?? vm.CashRegister;
        await vm.InitAsync();
        if (source is not null) vm.FillFrom(source, asEdit);
        ModalHost.ShowDialog(new OperationWindow { DataContext = vm });
        if (vm.Posted) await LoadAsync();
    }
}

/// <summary>Условия фильтра журнала (кроме периода и поиска).</summary>
public sealed record FinanceFilter(int? CashRegisterId = null, int? OperationType = null, int? CounterpartyId = null)
{
    public int ActiveCount => (CashRegisterId is null ? 0 : 1) + (OperationType is null ? 0 : 1) + (CounterpartyId is null ? 0 : 1);

    /// <summary>Типы операций фильтра: название, направление (null — любое), виды (null — любые).</summary>
    public static readonly (string Name, CashDirection? Direction, CashOperationKind[]? Kinds)[] OperationTypes =
    [
        ("Доходы", CashDirection.Income, null),
        ("Расходы", CashDirection.Expense, null),
        ("Приход денег", CashDirection.Income, [CashOperationKind.Manual]),
        ("Расход денег", CashDirection.Expense, [CashOperationKind.Manual]),
        ("Продажа", null, [CashOperationKind.Sale]),
        ("Возврат покупателю", null, [CashOperationKind.SaleReturn]),
        ("Оплата поставщику", null, [CashOperationKind.SupplierPayment]),
        ("Возврат от поставщика", null, [CashOperationKind.PurchaseReturn]),
        ("Перемещение между кассами", null, [CashOperationKind.TransferIn, CashOperationKind.TransferOut]),
        ("Платёж контрагенту", CashDirection.Expense, [CashOperationKind.CounterpartyPayment]),
        ("Платёж от контрагента", CashDirection.Income, [CashOperationKind.CounterpartyPayment]),
        ("Начисление контрагенту", null, [CashOperationKind.CounterpartyAccrual]),
    ];

    public CashDirection? Direction => OperationType is { } i ? OperationTypes[i].Direction : null;
    public CashOperationKind[]? Kinds => OperationType is { } i ? OperationTypes[i].Kinds : null;
}

/// <summary>Пункт выпадающего списка фильтра; Value = null — «Все».</summary>
public sealed record FilterChoice<T>(string Name, T? Value) where T : struct
{
    public override string ToString() => Name;
}

/// <summary>Модель окна «Фильтр» журнала операций.</summary>
public partial class FinanceFilterViewModel : ObservableObject, ICloseRequest
{
    public FinanceFilterViewModel(FinanceFilter current, IEnumerable<CashRegister> cash, IEnumerable<Counterparty> counterparties)
    {
        CashChoices = [new(L.T("Все кассы"), null), .. cash.Select(c => new FilterChoice<int>(c.Name, c.Id))];
        DirectionChoices = [new(L.T("Все операции"), null), .. FinanceFilter.OperationTypes.Select((t, i) => new FilterChoice<int>(L.T(t.Name), i))];
        CounterpartyChoices = [new(L.T("Все контрагенты"), null),
            .. counterparties.Where(c => c.IsActive || c.Id == current.CounterpartyId).Select(c => new FilterChoice<int>(c.Name, c.Id))];
        Load(current);
    }

    public string Title => L.T("Фильтр");
    public double DialogWidth => 440;
    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<FilterChoice<int>> CashChoices { get; }
    public IReadOnlyList<FilterChoice<int>> DirectionChoices { get; }
    public IReadOnlyList<FilterChoice<int>> CounterpartyChoices { get; }

    [ObservableProperty] private FilterChoice<int>? _cash;
    [ObservableProperty] private FilterChoice<int>? _direction;
    [ObservableProperty] private FilterChoice<int>? _counterparty;
    /// <summary>Применённый фильтр; null — окно закрыто без изменений.</summary>
    public FinanceFilter? Result { get; private set; }

    private void Load(FinanceFilter f)
    {
        Cash = CashChoices.FirstOrDefault(c => c.Value == f.CashRegisterId) ?? CashChoices[0];
        Direction = DirectionChoices.FirstOrDefault(c => c.Value == f.OperationType) ?? DirectionChoices[0];
        Counterparty = CounterpartyChoices.FirstOrDefault(c => c.Value == f.CounterpartyId) ?? CounterpartyChoices[0];
    }

    [RelayCommand]
    private void Reset() => Load(new FinanceFilter());

    [RelayCommand]
    private void Apply()
    {
        Result = new FinanceFilter(Cash?.Value, Direction?.Value, Counterparty?.Value);
        CloseRequested?.Invoke(this, true);
    }
}
