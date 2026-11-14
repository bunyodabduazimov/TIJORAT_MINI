using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Строка журнала действий для таблицы.</summary>
public sealed record AuditRow(AuditLog Log, string ActionName, string ObjectType)
{
    public bool IsDelete => Log.Action is AuditAction.Delete or AuditAction.Archive;
    public bool IsCreate => Log.Action is AuditAction.Create or AuditAction.Restore;
}


/// <summary>Журнал действий: кто, когда и что создал, изменил, удалил (только для администратора).</summary>
public partial class AuditLogViewModel : PageViewModel
{
    private readonly AuditService _audit;
    private List<(int Id, string Name)> _users = [];

    public AuditLogViewModel(AuditService audit, IDialogService dialogs) : base(dialogs)
    {
        _audit = audit;
        Pager = new(Match);
    }

    public override string Title => L.T("Журнал действий");

    public ReportPager<AuditRow> Pager { get; }

    [ObservableProperty] private DateTime _from = DateTime.Today.AddDays(-7);
    [ObservableProperty] private DateTime _to = DateTime.Today;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private AuditRow? _selected;

    // ── Условия окна «Фильтр»: пользователь, действие, компьютер, объект ──
    public AuditFilter Filter { get; private set; } = new();
    public bool HasActiveFilter => Filter.ActiveCount > 0;
    public int ActiveFilterCount => Filter.ActiveCount;

    partial void OnSearchChanged(string value) => Pager.Refresh();

    public override async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            var users = await _audit.GetUsersAsync();
            _users = users.Select(u => (u.Id, string.IsNullOrWhiteSpace(u.FullName) ? u.Login : u.FullName)).ToList();
        });
        await LoadRowsAsync();
    }

    [RelayCommand]
    private Task LoadRowsAsync() => RunAsync(async () =>
    {
        if (To < From) (From, To) = (To, From);
        var logs = await _audit.GetAsync(From, To, null, null);
        Pager.SetSource(logs.Select(l => new AuditRow(l, ActionName(l.Action), TypeName(l.EntityType))).ToList());
    });

    [RelayCommand]
    private void OpenFilter()
    {
        var rows = Pager.Source;
        var vm = new AuditFilterViewModel(Filter, _users,
            rows.Select(r => r.Log.Machine).Append(Filter.Machine),
            rows.Select(r => r.ObjectType).Append(Filter.ObjectType));
        ModalHost.ShowDialog(new Views.FilterWindow { DataContext = vm });
        if (vm.Result is not { } applied) return;
        Filter = applied;
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(ActiveFilterCount));
        Pager.Refresh();
    }
    [RelayCommand]
    private Task SetPeriod(string period)
    {
        To = DateTime.Today;
        From = period switch
        {
            "today" => DateTime.Today,
            "week" => DateTime.Today.AddDays(-6),
            _ => DateTime.Today.AddMonths(-1).AddDays(1)
        };
        return LoadRowsAsync();
    }

    [RelayCommand]
    private void Export()
    {
        var path = Dialogs.SaveFile($"{L.T("Журнал")}_{From:yyyyMMdd}-{To:yyyyMMdd}.xlsx", ExcelExport.Filter);
        if (path is null) return;
        var lines = Pager.Filtered
            .Select(r => Row(r.Log.Date.ToString("dd.MM.yyyy HH:mm:ss"), r.Log.UserName, r.Log.Machine, r.ActionName,
                r.ObjectType, r.Log.EntityName, r.Log.Details))
            .Prepend(L.T("Дата;Пользователь;Компьютер;Действие;Объект;Название;Подробности"));
        try
        {
            ExcelExport.Save(path, lines, L.T("Журнал"));
            Dialogs.Info(L.F("Файл сохранён: {0}", path));
        }
        catch (IOException ex) { Dialogs.Error(ex.Message); }
    }

    private bool Match(AuditRow r)
    {
        var f = Filter;
        if (f.UserId is { } uid && r.Log.UserId != uid) return false;
        if (f.Action is { } act && r.Log.Action != act) return false;
        if (f.Machine is { } m && !string.Equals(r.Log.Machine, m, StringComparison.CurrentCultureIgnoreCase)) return false;
        if (f.ObjectType is { } o && r.ObjectType != o) return false;
        if (string.IsNullOrWhiteSpace(Search)) return true;
        var s = Search.Trim();
        return Has(r.Log.UserName) || Has(r.Log.EntityName) || Has(r.Log.Details) || Has(r.ObjectType) || Has(r.ActionName) || Has(r.Log.Machine);
        bool Has(string? v) => v?.Contains(s, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    private static string Row(params object?[] values) =>
        string.Join(';', values.Select(v => v is null ? "" : '"' + v.ToString()!.Replace("\"", "\"\"") + '"'));

    public static string ActionName(AuditAction a) => a switch
    {
        AuditAction.Create => L.T("Создание"),
        AuditAction.Update => L.T("Изменение"),
        AuditAction.Delete => L.T("Удаление"),
        AuditAction.Archive => L.T("В архив"),
        AuditAction.Restore => L.T("Из архива"),
        AuditAction.Login => L.T("Вход"),
        AuditAction.Logout => L.T("Выход"),
        _ => a.ToString()
    };

    public static string TypeName(string type) => type switch
    {
        nameof(Category) => L.T("Категория"),
        nameof(Product) => L.T("Товар"),
        nameof(ProductPhoto) => L.T("Фото товара"),
        nameof(CounterpartyGroup) => L.T("Группа контрагентов"),
        nameof(Counterparty) => L.T("Контрагент"),
        nameof(Warehouse) => L.T("Склад"),
        nameof(MeasureUnit) => L.T("Единица измерения"),
        nameof(ScaleDevice) => L.T("Весы"),
        nameof(CashRegister) => L.T("Касса"),
        nameof(FinanceArticle) => L.T("Статья"),
        nameof(Sale) => L.T("Продажа"),
        nameof(SaleReturn) => L.T("Возврат"),
        nameof(Purchase) => L.T("Закуп"),
        nameof(StockTransfer) => L.T("Перемещение"),
        nameof(InventoryCount) => L.T("Инвентаризация"),
        nameof(StockWriteOff) => L.T("Списание"),
        nameof(ProductionOrder) => L.T("Производство"),
        nameof(Recipe) => L.T("Техкарта"),
        nameof(CashTransaction) => L.T("Кассовая операция"),
        nameof(User) => L.T("Пользователь"),
        nameof(CompanySettings) => L.T("Настройки компании"),
        "Session" => L.T("Сеанс"),
        _ => type
    };
}

/// <summary>Условия фильтра журнала действий (кроме периода и поиска).</summary>
public sealed record AuditFilter(int? UserId = null, AuditAction? Action = null, string? Machine = null, string? ObjectType = null)
{
    public int ActiveCount => (UserId is null ? 0 : 1) + (Action is null ? 0 : 1) + (Machine is null ? 0 : 1) + (ObjectType is null ? 0 : 1);
}

/// <summary>Модель окна «Фильтр» журнала действий.</summary>
public partial class AuditFilterViewModel : ObservableObject, ICloseRequest
{
    public AuditFilterViewModel(AuditFilter current, IEnumerable<(int Id, string Name)> users,
        IEnumerable<string?> machines, IEnumerable<string?> objectTypes)
    {
        UserChoices = [new(L.T("Все пользователи"), null), .. users.Select(u => new FilterChoice<int>(u.Name, u.Id))];
        ActionChoices = [new(L.T("Все действия"), null),
            .. Enum.GetValues<AuditAction>().Select(a => new FilterChoice<AuditAction>(AuditLogViewModel.ActionName(a), a))];
        MachineChoices = [new(L.T("Все компьютеры"), null), .. Distinct(machines)];
        ObjectChoices = [new(L.T("Все объекты"), null), .. Distinct(objectTypes)];
        Load(current);
    }

    private static IEnumerable<AuditTextChoice> Distinct(IEnumerable<string?> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.CurrentCultureIgnoreCase)
              .Order(StringComparer.CurrentCultureIgnoreCase).Select(v => new AuditTextChoice(v!, v));

    public string Title => L.T("Фильтр");
    public double DialogWidth => 440;
    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<FilterChoice<int>> UserChoices { get; }
    public IReadOnlyList<FilterChoice<AuditAction>> ActionChoices { get; }
    public IReadOnlyList<AuditTextChoice> MachineChoices { get; }
    public IReadOnlyList<AuditTextChoice> ObjectChoices { get; }

    [ObservableProperty] private FilterChoice<int>? _user;
    [ObservableProperty] private FilterChoice<AuditAction>? _action;
    [ObservableProperty] private AuditTextChoice? _machine;
    [ObservableProperty] private AuditTextChoice? _object;
    /// <summary>Применённый фильтр; null — окно закрыто без изменений.</summary>
    public AuditFilter? Result { get; private set; }

    private void Load(AuditFilter f)
    {
        User = UserChoices.FirstOrDefault(c => c.Value == f.UserId) ?? UserChoices[0];
        Action = ActionChoices.FirstOrDefault(c => Nullable.Equals(c.Value, f.Action)) ?? ActionChoices[0];
        Machine = MachineChoices.FirstOrDefault(c => c.Value == f.Machine) ?? MachineChoices[0];
        Object = ObjectChoices.FirstOrDefault(c => c.Value == f.ObjectType) ?? ObjectChoices[0];
    }

    [RelayCommand]
    private void Reset() => Load(new AuditFilter());

    [RelayCommand]
    private void Apply()
    {
        Result = new AuditFilter(User?.Value, Action?.Value, Machine?.Value, Object?.Value);
        CloseRequested?.Invoke(this, true);
    }
}

/// <summary>Текстовый пункт фильтра; Value = null — «Все».</summary>
public sealed record AuditTextChoice(string Name, string? Value)
{
    public override string ToString() => Name;
}
