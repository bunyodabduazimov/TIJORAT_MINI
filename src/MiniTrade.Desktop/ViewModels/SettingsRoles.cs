using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Domain;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Строка таблицы ролей.</summary>
public sealed record RoleRow(AccessRole Role, int Users, int Granted, string Summary)
{
    public string Name => Role.Name;
    public bool IsAdmin => Role.IsAdmin;
    public string PermissionsText => IsAdmin ? L.T("Все") : $"{Granted} / {PermissionCatalog.AllKeys.Count()}";
}

/// <summary>Флажок действия в матрице прав.</summary>
public sealed partial class PermissionActionOption(PermAction action) : ObservableObject
{
    public string Key { get; } = action.Key;
    public string Name { get; } = L.T(action.Name);
    [ObservableProperty] private bool _isChecked;
    public event EventHandler? Changed;
    partial void OnIsCheckedChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Флажок «все действия» (три состояния) над набором действий.</summary>
public abstract class PermissionSetOption : ObservableObject
{
    private bool _updating;

    protected void Track(IEnumerable<PermissionActionOption> actions)
    {
        Covered = actions.ToList();
        foreach (var a in Covered) a.Changed += (_, _) =>
        {
            if (!_updating) OnPropertyChanged(nameof(AllChecked));
            OnPropertyChanged(nameof(Counter));
        };
    }

    /// <summary>Узел дерева раскрыт (видны вложенные права).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
    private bool _isExpanded;

    /// <summary>«3 / 5» — сколько прав узла выбрано.</summary>
    public string Counter => $"{Covered.Count(a => a.IsChecked)} / {Covered.Count}";

    /// <summary>Все действия под этим флажком (у модуля — вместе с подразделами).</summary>
    public IReadOnlyList<PermissionActionOption> Covered { get; private set; } = [];

    /// <summary>true — все действия, false — ни одного, null — часть.</summary>
    public bool? AllChecked
    {
        get => Covered.All(a => a.IsChecked) ? true : Covered.Any(a => a.IsChecked) ? null : false;
        set
        {
            _updating = true;
            // Щелчок: «все» → снять всё; «нет» или «частично» → включить всё
            // (значение из IsThreeState-цикла не используем: из «все» WPF переходит в null)
            var on = !Covered.All(a => a.IsChecked);
            foreach (var a in Covered) a.IsChecked = on;
            _updating = false;
            OnPropertyChanged();
        }
    }
}

/// <summary>Подраздел модуля в матрице прав (например, «Финансы → Приход»).</summary>
public sealed class PermissionGroupOption : PermissionSetOption
{
    public PermissionGroupOption(PermGroup group)
    {
        Name = L.T(group.Name);
        Actions = group.Actions.Select(a => new PermissionActionOption(a)).ToList();
        Track(Actions);
    }

    public string Name { get; }
    public IReadOnlyList<PermissionActionOption> Actions { get; }
}

/// <summary>Модуль в матрице прав: флажок «всё», собственные действия и подразделы.</summary>
public sealed class PermissionModuleOption : PermissionSetOption
{
    public PermissionModuleOption(PermModule module)
    {
        Name = L.T(module.Name);
        Actions = module.Actions.Select(a => new PermissionActionOption(a)).ToList();
        Groups = module.SubGroups.Select(g => new PermissionGroupOption(g)).ToList();
        Track(Actions.Concat(Groups.SelectMany(g => g.Actions)));
    }

    public string Name { get; }
    public IReadOnlyList<PermissionActionOption> Actions { get; }
    public IReadOnlyList<PermissionGroupOption> Groups { get; }
    public bool HasGroups => Groups.Count > 0;
}

/// <summary>Вкладка «Роли»: список ролей и матрица прав «модуль × действие».</summary>
public partial class SettingsViewModel
{
    public ObservableCollection<RoleRow> RoleRows { get; } = new();

    public IReadOnlyList<PermissionModuleOption> PermissionModules { get; } =
        PermissionCatalog.Modules.Select(m => new PermissionModuleOption(m)).ToList();

    [ObservableProperty] private RoleRow? _selectedRole;
    [ObservableProperty] private int _editingRoleEntityId;
    [ObservableProperty] private string? _roleName;
    [ObservableProperty] private bool _roleIsAdmin;

    public string RoleEditorTitle => EditingRoleEntityId == 0 ? L.T("Новая роль") : L.T("Роль");
    partial void OnEditingRoleEntityIdChanged(int value) => OnPropertyChanged(nameof(RoleEditorTitle));

    partial void OnSelectedRoleChanged(RoleRow? value)
    {
        if (value is not null) OpenRole(value.Role, copy: false);
    }

    private async Task LoadRolesCore()
    {
        var roles = await settings.GetRolesAsync();
        var counts = await settings.GetRoleUserCountsAsync();
        var selectedId = SelectedRole?.Role.Id;
        Fill(RoleRows, roles.Select(r =>
        {
            var set = r.PermissionSet();
            return new RoleRow(r, counts.GetValueOrDefault(r.Id), set.Count, Summarize(r, set));
        }));
        OnPropertyChanged(nameof(EditingRoleAccess));
        var keep = RoleRows.FirstOrDefault(r => r.Role.Id == selectedId);
        if (keep is not null) SelectedRole = keep;
        else if (EditingRoleEntityId == 0 && RoleName is null) NewRole();
    }

    /// <summary>«Продажи: Просмотр, Продажа; Склад: Просмотр» — для формы пользователя.</summary>
    private static string Summarize(AccessRole role, HashSet<string> set)
    {
        if (role.IsAdmin) return L.T("Все разделы и действия");
        var parts = PermissionCatalog.Modules
            .Select(m => (m, acts: m.Actions.Where(a => set.Contains(a.Key)).Select(a => L.T(a.Name))
                .Concat(m.SubGroups.Where(g => g.Actions.Any(a => set.Contains(a.Key))).Select(g => L.T(g.Name))).ToList()))
            .Where(x => x.acts.Count > 0)
            .Select(x => x.m.AllActions.All(a => set.Contains(a.Key)) ? L.T(x.m.Name) : $"{L.T(x.m.Name)}: {string.Join(", ", x.acts)}")
            .ToList();
        return parts.Count == 0 ? L.T("Нет доступа к разделам") : string.Join("; ", parts);
    }

    private void OpenRole(AccessRole? role, bool copy)
    {
        EditingRoleEntityId = copy || role is null ? 0 : role.Id;
        RoleName = role is null ? null : copy ? L.F("{0} (копия)", role.Name) : role.Name;
        HookPermissionNodes();
        RoleIsAdmin = !copy && role?.IsAdmin == true;
        var set = role?.PermissionSet() ?? [];
        var all = role?.IsAdmin == true;
        foreach (var a in PermissionModules.SelectMany(m => m.Covered)) a.IsChecked = all || set.Contains(a.Key);
    }

    [RelayCommand]
    private void NewRole()
    {
        SelectedRole = null;
        OpenRole(null, copy: false);
    }

    [RelayCommand]
    private void EditRole(RoleRow? row)
    {
        row ??= SelectedRole;
        if (row is null) return;
        if (SelectedRole == row) OpenRole(row.Role, copy: false);
        else SelectedRole = row;
    }

    [RelayCommand]
    private void CopyRole(RoleRow? row)
    {
        row ??= SelectedRole;
        if (row is null) return;
        SelectedRole = null;
        OpenRole(row.Role, copy: true);
    }

    [RelayCommand]
    private async Task DeleteRoleAsync(RoleRow? row)
    {
        row ??= SelectedRole;
        if (row is null) return;
        if (!Dialogs.Confirm(L.F("Удалить роль «{0}»?", row.Name))) return;
        if (!await RunAsync(() => settings.DeleteRoleAsync(row.Role.Id))) return;
        SelectedRole = null;
        NewRole();
        await RunAsync(LoadRolesCore);
    }

    // ── Переключатели над деревом прав: «Развернуть всё ⇄ Свернуть всё», «Выбрать всё ⇄ Снять всё» ──
    private bool _permissionsHooked;

    private IEnumerable<PermissionSetOption> PermissionNodes => PermissionModules.Concat<PermissionSetOption>(PermissionModules.SelectMany(m => m.Groups));

    private bool AllExpanded => PermissionNodes.All(n => n.IsExpanded);
    private bool AllPermissionsChecked => PermissionModules.All(m => m.AllChecked == true);

    public string ExpandToggleText => AllExpanded ? L.T("Свернуть всё") : L.T("Развернуть всё");
    public string CheckToggleText => AllPermissionsChecked ? L.T("Снять всё") : L.T("Выбрать всё");

    /// <summary>Подписка на узлы: подписи переключателей следуют за флажками и раскрытием.</summary>
    private void HookPermissionNodes()
    {
        if (_permissionsHooked) return;
        _permissionsHooked = true;
        foreach (var n in PermissionNodes)
            n.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PermissionSetOption.IsExpanded)) OnPropertyChanged(nameof(ExpandToggleText));
                else if (e.PropertyName is nameof(PermissionSetOption.AllChecked) or nameof(PermissionSetOption.Counter))
                    OnPropertyChanged(nameof(CheckToggleText));
            };
    }

    [RelayCommand]
    private void ToggleExpandPermissions()
    {
        var expand = !AllExpanded;
        foreach (var n in PermissionNodes) n.IsExpanded = expand;
    }

    [RelayCommand]
    private void ToggleCheckPermissions()
    {
        var check = !AllPermissionsChecked;
        foreach (var m in PermissionModules) m.AllChecked = check;
    }

    [RelayCommand]
    private async Task SaveRoleAsync()
    {
        var role = new AccessRole { Id = EditingRoleEntityId, Name = RoleName ?? "" };
        var keys = PermissionModules.SelectMany(m => m.Covered).Where(a => a.IsChecked).Select(a => a.Key).ToList();
        AccessRole? saved = null;
        if (!await RunAsync(async () => saved = await settings.SaveRoleAsync(role, keys))) return;
        EditingRoleEntityId = saved!.Id;
        await RunAsync(LoadRolesCore);
        SelectedRole = RoleRows.FirstOrDefault(r => r.Role.Id == saved.Id);
        Fill(Users, await settings.GetUsersAsync());
        Dialogs.Info(L.T("Роль сохранена. Пользователи с этой ролью увидят изменения при следующем входе."));
    }
}
