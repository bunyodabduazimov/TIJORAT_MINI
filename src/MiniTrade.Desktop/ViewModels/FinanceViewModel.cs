using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.Views;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>
/// Кассы и журнал операций (постранично). Операции создаются из меню «Создать» в заголовке страницы
/// и оформляются в модальных окнах (Views/OperationWindow); условия журнала — в окне «Фильтр».
/// </summary>
public partial class FinanceViewModel(
    FinanceService finance, CatalogService catalog, SettingsService settings, IDialogService dialogs, ICatalogDialogs catalogDialogs) : PageViewModel(dialogs)
{
    public override string Title => L.T("Финансы");

    public ObservableCollection<CashRegister> CashRegisters { get; } = new();
    public ObservableCollection<CashTransaction> Operations { get; } = new();

    [ObservableProperty] private decimal _totalCash;
    [ObservableProperty] private decimal _periodIncome;
    [ObservableProperty] private decimal _periodExpense;

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
        var f = new CashOperationFilter(From, To, Filter.CashRegisterId, Filter.Direction, Filter.CounterpartyId, Search);
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

    private static bool CanModify(CashTransaction? t) => t is not null && FinanceService.CanModify(t);
    private bool CanCopy(CashTransaction? t) => t is not null && FormFor(t) is not null;

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

    [RelayCommand(CanExecute = nameof(CanModify))]
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
public sealed record FinanceFilter(int? CashRegisterId = null, CashDirection? Direction = null, int? CounterpartyId = null)
{
    public int ActiveCount => (CashRegisterId is null ? 0 : 1) + (Direction is null ? 0 : 1) + (CounterpartyId is null ? 0 : 1);
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
        DirectionChoices = [new(L.T("Все операции"), null), new(L.T("Доходы"), CashDirection.Income), new(L.T("Расходы"), CashDirection.Expense)];
        CounterpartyChoices = [new(L.T("Все контрагенты"), null),
            .. counterparties.Where(c => c.IsActive || c.Id == current.CounterpartyId).Select(c => new FilterChoice<int>(c.Name, c.Id))];
        Load(current);
    }

    public string Title => L.T("Фильтр");
    public double DialogWidth => 440;
    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<FilterChoice<int>> CashChoices { get; }
    public IReadOnlyList<FilterChoice<CashDirection>> DirectionChoices { get; }
    public IReadOnlyList<FilterChoice<int>> CounterpartyChoices { get; }

    [ObservableProperty] private FilterChoice<int>? _cash;
    [ObservableProperty] private FilterChoice<CashDirection>? _direction;
    [ObservableProperty] private FilterChoice<int>? _counterparty;
    /// <summary>Применённый фильтр; null — окно закрыто без изменений.</summary>
    public FinanceFilter? Result { get; private set; }

    private void Load(FinanceFilter f)
    {
        Cash = CashChoices.FirstOrDefault(c => c.Value == f.CashRegisterId) ?? CashChoices[0];
        Direction = DirectionChoices.FirstOrDefault(c => c.Value == f.Direction) ?? DirectionChoices[0];
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
