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

public sealed record AuditOption<T>(T? Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Журнал действий: кто, когда и что создал, изменил, удалил (только для администратора).</summary>
public partial class AuditLogViewModel : PageViewModel
{
    private readonly AuditService _audit;

    public AuditLogViewModel(AuditService audit, IDialogService dialogs) : base(dialogs)
    {
        _audit = audit;
        Pager = new(Match);
        Actions = [new(null, L.T("Все действия")), ..Enum.GetValues<AuditAction>().Select(a => new AuditOption<AuditAction?>(a, ActionName(a)))];
        _selectedAction = Actions[0];
        Users = [new(null, L.T("Все пользователи"))];
        _selectedUser = Users[0];
    }

    public override string Title => L.T("Журнал действий");

    public ReportPager<AuditRow> Pager { get; }
    public List<AuditOption<AuditAction?>> Actions { get; }
    public System.Collections.ObjectModel.ObservableCollection<AuditOption<int?>> Users { get; }

    [ObservableProperty] private DateTime _from = DateTime.Today.AddDays(-7);
    [ObservableProperty] private DateTime _to = DateTime.Today;
    [ObservableProperty] private AuditOption<AuditAction?> _selectedAction;
    [ObservableProperty] private AuditOption<int?> _selectedUser;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private AuditRow? _selected;

    partial void OnSearchChanged(string value) => Pager.Refresh();
    partial void OnSelectedActionChanged(AuditOption<AuditAction?> value) => _ = LoadRowsAsync();
    partial void OnSelectedUserChanged(AuditOption<int?> value) => _ = LoadRowsAsync();

    public override async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            var users = await _audit.GetUsersAsync();
            var current = SelectedUser?.Value;
            Users.Clear();
            Users.Add(new(null, L.T("Все пользователи")));
            foreach (var u in users) Users.Add(new(u.Id, string.IsNullOrWhiteSpace(u.FullName) ? u.Login : u.FullName));
#pragma warning disable MVVMTK0034 // без повторной загрузки — строки грузятся ниже
            _selectedUser = Users.FirstOrDefault(u => u.Value == current) ?? Users[0];
#pragma warning restore MVVMTK0034
            OnPropertyChanged(nameof(SelectedUser));
        });
        await LoadRowsAsync();
    }

    [RelayCommand]
    private Task LoadRowsAsync() => RunAsync(async () =>
    {
        if (To < From) (From, To) = (To, From);
        var logs = await _audit.GetAsync(From, To, SelectedUser?.Value, SelectedAction?.Value);
        Pager.SetSource(logs.Select(l => new AuditRow(l, ActionName(l.Action), TypeName(l.EntityType))).ToList());
    });

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
        var path = Dialogs.SaveFile($"{L.T("Журнал")}_{From:yyyyMMdd}-{To:yyyyMMdd}.csv", "CSV (*.csv)|*.csv");
        if (path is null) return;
        var lines = Pager.Filtered
            .Select(r => Row(r.Log.Date.ToString("dd.MM.yyyy HH:mm:ss"), r.Log.UserName, r.Log.Machine, r.ActionName,
                r.ObjectType, r.Log.EntityName, r.Log.Details))
            .Prepend(L.T("Дата;Пользователь;Компьютер;Действие;Объект;Название;Подробности"));
        try
        {
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
            Dialogs.Info(L.F("Файл сохранён: {0}", path));
        }
        catch (IOException ex) { Dialogs.Error(ex.Message); }
    }

    private bool Match(AuditRow r)
    {
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
        nameof(CashRegister) => L.T("Касса"),
        nameof(FinanceArticle) => L.T("Статья"),
        nameof(Sale) => L.T("Продажа"),
        nameof(SaleReturn) => L.T("Возврат"),
        nameof(Purchase) => L.T("Закуп"),
        nameof(StockTransfer) => L.T("Перемещение"),
        nameof(InventoryCount) => L.T("Инвентаризация"),
        nameof(CashTransaction) => L.T("Кассовая операция"),
        nameof(User) => L.T("Пользователь"),
        nameof(CompanySettings) => L.T("Настройки компании"),
        "Session" => L.T("Сеанс"),
        _ => type
    };
}
