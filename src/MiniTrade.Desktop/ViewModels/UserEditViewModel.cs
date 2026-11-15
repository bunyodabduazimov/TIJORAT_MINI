using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Склад или касса с галочкой «доступен пользователю».</summary>
public sealed partial class AccessPick(int id, string name) : ObservableObject
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isChecked;
    public event EventHandler? Changed;
    partial void OnIsCheckedChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Модальное окно добавления/изменения пользователя: данные, роль, пароль и личные настройки доступа —
/// склад и касса по умолчанию, доступные склады и кассы, лимит скидки, срок доступа.
/// </summary>
public partial class UserEditViewModel : ViewModelBase, ICloseRequest
{
    private readonly SettingsService _settings;

    public UserEditViewModel(SettingsService settings, IDialogService dialogs, User? source,
        IReadOnlyList<RoleRow> roles, IReadOnlyList<Warehouse> warehouses, IReadOnlyList<CashRegister> cashRegisters) : base(dialogs)
    {
        _settings = settings;
        Roles = roles;
        User = source is null
            ? new User { AccessRoleId = roles.FirstOrDefault(r => !r.Role.IsAdmin)?.Role.Id }
            : new User
            {
                Id = source.Id, Login = source.Login, FullName = source.FullName, Role = source.Role, IsActive = source.IsActive,
                Phone = source.Phone, Note = source.Note, AccessRoleId = source.AccessRoleId,
                DefaultWarehouseId = source.DefaultWarehouseId, DefaultCashRegisterId = source.DefaultCashRegisterId,
                AllowedWarehouseIds = source.AllowedWarehouseIds, AllowedCashRegisterIds = source.AllowedCashRegisterIds,
                MaxDiscountPercent = source.MaxDiscountPercent, AccessUntil = source.AccessUntil,
                WorkFrom = source.WorkFrom, WorkTo = source.WorkTo
            };
        _workFrom = FormatTime(User.WorkFrom);
        _workTo = FormatTime(User.WorkTo);
        _roleId = User.AccessRoleId;
        _maxDiscount = User.MaxDiscountPercent?.ToString("0.##") ?? "";
        _accessUntil = User.AccessUntil;

        var allowedWh = User.ParseIds(User.AllowedWarehouseIds);
        var allowedCash = User.ParseIds(User.AllowedCashRegisterIds);
        _allWarehouses = allowedWh is null;
        _allCashRegisters = allowedCash is null;
        foreach (var w in warehouses)
            Warehouses.Add(Pick(w.Id, w.Name, allowedWh, RefreshWarehouseDefaults));
        foreach (var c in cashRegisters)
            CashRegisters.Add(Pick(c.Id, c.Name, allowedCash, RefreshCashDefaults));
        RefreshWarehouseDefaults();
        RefreshCashDefaults();
        _defaultWarehouse = DefaultWarehouseChoices.FirstOrDefault(w => w.Id == User.DefaultWarehouseId);
        _defaultCashRegister = DefaultCashChoices.FirstOrDefault(c => c.Id == User.DefaultCashRegisterId);
    }

    private static AccessPick Pick(int id, string name, HashSet<int>? allowed, Action changed)
    {
        var p = new AccessPick(id, name) { IsChecked = allowed?.Contains(id) ?? true };
        p.Changed += (_, _) => changed();
        return p;
    }

    public double DialogWidth => 760;
    public event EventHandler<bool>? CloseRequested;

    public User User { get; }
    public bool IsNew => User.Id == 0;
    public string Title => IsNew ? L.T("Новый пользователь") : L.F("Пользователь «{0}»", User.Login);
    public string PasswordLabel => IsNew ? L.T("Пароль *") : L.T("Новый пароль (оставьте пустым, чтобы не менять)");

    public IReadOnlyList<RoleRow> Roles { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleSummary))]
    private int? _roleId;
    public string RoleSummary => Roles.FirstOrDefault(r => r.Role.Id == RoleId)?.Summary ?? "";

    [ObservableProperty] private string? _password;
    [ObservableProperty] private string _maxDiscount;
    [ObservableProperty] private DateTime? _accessUntil;
    [ObservableProperty] private string _workFrom;
    [ObservableProperty] private string _workTo;

    private static string FormatTime(TimeSpan? t) => t is { } v ? v.ToString(@"hh\:mm") : "";

    /// <summary>«8», «8:30», «08:30», «0830» → время; пусто → null.</summary>
    private static bool TryParseTime(string? text, out TimeSpan? result)
    {
        result = null;
        var s = (text ?? "").Trim().Replace('.', ':');
        if (s.Length == 0) return true;
        if (!s.Contains(':')) s = s.Length <= 2 ? s + ":00" : s.Length is 3 or 4 ? s[..^2] + ":" + s[^2..] : s;
        var parts = s.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)
            || h is < 0 or > 23 || m is < 0 or > 59) return false;
        result = new TimeSpan(h, m, 0);
        return true;
    }

    // ── Склады ──
    public ObservableCollection<AccessPick> Warehouses { get; } = new();
    public ObservableCollection<AccessPick> DefaultWarehouseChoices { get; } = new();
    [ObservableProperty] private AccessPick? _defaultWarehouse;
    /// <summary>Нет ограничения: доступны все склады, в том числе новые.</summary>
    [ObservableProperty] private bool _allWarehouses;
    partial void OnAllWarehousesChanged(bool value) => RefreshWarehouseDefaults();

    // ── Кассы ──
    public ObservableCollection<AccessPick> CashRegisters { get; } = new();
    public ObservableCollection<AccessPick> DefaultCashChoices { get; } = new();
    [ObservableProperty] private AccessPick? _defaultCashRegister;
    [ObservableProperty] private bool _allCashRegisters;
    partial void OnAllCashRegistersChanged(bool value) => RefreshCashDefaults();

    /// <summary>По умолчанию можно выбрать только среди доступных.</summary>
    private void RefreshWarehouseDefaults() =>
        DefaultWarehouse = Refill(DefaultWarehouseChoices, Warehouses, AllWarehouses, DefaultWarehouse);

    private void RefreshCashDefaults() =>
        DefaultCashRegister = Refill(DefaultCashChoices, CashRegisters, AllCashRegisters, DefaultCashRegister);

    private static AccessPick? Refill(ObservableCollection<AccessPick> target, IEnumerable<AccessPick> source, bool all, AccessPick? current)
    {
        var items = source.Where(p => all || p.IsChecked).ToList();
        target.Clear();
        foreach (var p in items) target.Add(p);
        return current is not null && items.Contains(current) ? current : null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        decimal? max = null;
        var text = (MaxDiscount ?? "").Trim().Replace(',', '.');
        if (text.Length > 0)
        {
            if (!decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var m))
            { Dialogs.Error(L.T("Максимальная скидка — число от 0 до 100.")); return; }
            max = m;
        }
        if (!TryParseTime(WorkFrom, out var workFrom) || !TryParseTime(WorkTo, out var workTo))
        { Dialogs.Error(L.T("Время работы — в формате ЧЧ:ММ, например 08:00.")); return; }
        if (!AllWarehouses && !Warehouses.Any(w => w.IsChecked)) { Dialogs.Error(L.T("Отметьте хотя бы один доступный склад.")); return; }
        if (!AllCashRegisters && !CashRegisters.Any(c => c.IsChecked)) { Dialogs.Error(L.T("Отметьте хотя бы одну доступную кассу.")); return; }

        User.AccessRoleId = RoleId;
        User.MaxDiscountPercent = max;
        User.AccessUntil = AccessUntil;
        User.WorkFrom = workFrom;
        User.WorkTo = workTo;
        User.AllowedWarehouseIds = AllWarehouses ? null : User.JoinIds(Warehouses.Where(w => w.IsChecked).Select(w => w.Id));
        User.AllowedCashRegisterIds = AllCashRegisters ? null : User.JoinIds(CashRegisters.Where(c => c.IsChecked).Select(c => c.Id));
        User.DefaultWarehouseId = DefaultWarehouse?.Id;
        User.DefaultCashRegisterId = DefaultCashRegister?.Id;

        var password = Password;
        if (!await RunAsync(() => _settings.SaveUserAsync(User, password))) return;
        // Сменили пароль пользователю, чей пароль запомнен на этом компьютере, — обновляем сохранённый
        if (!string.IsNullOrEmpty(password) && SavedLogin.Load() is { } saved
            && string.Equals(saved.Login, User.Login, StringComparison.OrdinalIgnoreCase))
            SavedLogin.Save(saved.Login, password);
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
