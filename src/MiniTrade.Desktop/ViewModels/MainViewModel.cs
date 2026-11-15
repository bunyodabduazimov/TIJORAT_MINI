using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.Licensing;
using MiniTrade.Licensing;

namespace MiniTrade.Desktop.ViewModels;

public sealed record NavItem(string Title, string Icon, Type PageType);

/// <summary>Оболочка приложения: боковое меню и текущая страница.</summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly LicenseManager _license;
    private readonly IDialogService _dialogs;

    public MainViewModel(IServiceProvider services, UserSession session, LicenseManager license, IDialogService dialogs)
    {
        _services = services;
        _license = license;
        _dialogs = dialogs;
        _isMenuCollapsed = App.Ui.MenuCollapsed;
        UserName = session.CurrentUser?.FullName ?? "";
        UserRole = session.CurrentUser is { IsAdmin: true } ? L.T("Администратор") : session.CurrentUser?.AccessRole?.Name ?? "";
        NavItems =
        [
            new(L.T("Обзор"), "\U000F0A1D", typeof(DashboardViewModel)),
            new(L.T("Финансы"), "\U000F019B", typeof(FinanceViewModel)),
            new(L.T("Продажи"), "\U000F0111", typeof(SalesViewModel)),
            new(L.T("Закупки"), "\U000F129B", typeof(PurchasesViewModel)),
            new(L.T("Склад"), "\U000F03D7", typeof(WarehouseViewModel)),
            new(L.T("Производство"), "\U000F0247", typeof(ProductionViewModel)),
            new(L.T("Каталоги"), "\U000F1CCC", typeof(CatalogViewModel)),
            new(L.T("Аналитика"), "\U000F0127", typeof(ReportsViewModel)),
            new(L.T("Настройки"), "\U000F08BB", typeof(SettingsViewModel)),
        ];
        // Журнал действий — внизу меню
        NavItems.Add(new(L.T("История действий"), "\U000F02DA", typeof(AuditLogViewModel)));
        // Разделы, на которые у пользователя нет прав, скрываем («Настройки» доступны всем — смена своего пароля)
        if (session.CurrentUser is { } user)
            foreach (var item in NavItems.Where(n => PermissionOf(n.PageType) is { } p && !user.Can(p)).ToList())
                NavItems.Remove(item);
        license.StateChanged += OnLicenseChanged;
        SelectedNav = NavItems.FirstOrDefault(n => n.PageType.Name == App.Ui.StartPage) ?? NavItems[0];
    }

    /// <summary>Право, необходимое для раздела (null — раздел доступен всем).</summary>
    public static string? PermissionOf(Type page) => page.Name switch
    {
        nameof(DashboardViewModel) => Core.Domain.Perm.DashboardView,
        nameof(FinanceViewModel) => Core.Domain.Perm.FinanceView,
        nameof(SalesViewModel) => Core.Domain.Perm.SalesView,
        nameof(PurchasesViewModel) => Core.Domain.Perm.PurchasesView,
        nameof(WarehouseViewModel) => Core.Domain.Perm.WarehouseView,
        nameof(ProductionViewModel) => Core.Domain.Perm.ProductionView,
        nameof(CatalogViewModel) => Core.Domain.Perm.CatalogView,
        nameof(ReportsViewModel) => Core.Domain.Perm.ReportsView,
        nameof(AuditLogViewModel) => Core.Domain.Perm.AuditView,
        _ => null
    };

    private void OnLicenseChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(LicenseText));

    /// <summary>Отписка от лицензии, когда окно пересоздаётся (смена языка).</summary>
    /// <summary>Первая буква имени — для кружка-аватара.</summary>
    public string UserInitial => string.IsNullOrWhiteSpace(UserName) ? "?" : UserName.Trim()[..1].ToUpperInvariant();

    /// <summary>Меню свёрнуто до иконок (запоминается в настройках интерфейса).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MenuToggleText))]
    private bool _isMenuCollapsed;

    public string MenuToggleText => IsMenuCollapsed ? L.T("Развернуть меню") : L.T("Свернуть меню");

    partial void OnIsMenuCollapsedChanged(bool value)
    {
        App.Ui.MenuCollapsed = value;
        App.Ui.Save();
    }

    [RelayCommand]
    private void ToggleMenu() => IsMenuCollapsed = !IsMenuCollapsed;

    /// <summary>Выход из аккаунта: снова окно входа (лицензия и база остаются открытыми).</summary>
    [RelayCommand]
    private void SignOut()
    {
        if (!_dialogs.Confirm(L.T("Выйти из аккаунта? Несохранённые данные на открытой странице будут потеряны."), L.T("Выход")))
            return;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(App.SignOut);
    }

    /// <summary>Окно «Мой аккаунт»: профиль и смена пароля (интерфейс — в «Настройки → Компания»).</summary>
    [RelayCommand]
    private void OpenProfile()
    {
        var vm = new ProfileViewModel(_services.GetRequiredService<SettingsService>(), _services.GetRequiredService<UserSession>(), _dialogs);
        ModalHost.ShowDialog(new Views.ProfileWindow { DataContext = vm });
    }

    /// <summary>После смены языка: новое главное окно снова на «Настройки → Компания» (карточки интерфейса).</summary>
    public static void ReloadOnInterfaceSettings()
    {
        var main = App.ReloadMainWindow();
        main.Navigate<SettingsViewModel>(s => s.SelectedTab = SettingsViewModel.CompanyTab);
    }

    public void Dispose() => _license.StateChanged -= OnLicenseChanged;

    /// <summary>Открыть страницу из кода (например, «Настройки» после смены языка).</summary>
    public void Navigate<TPage>(Action<TPage>? configure = null) where TPage : PageViewModel
    {
        SelectedNav = NavItems.FirstOrDefault(n => n.PageType == typeof(TPage)) ?? SelectedNav;
        if (CurrentPage is TPage page) configure?.Invoke(page);
    }

    /// <summary>Открыть раздел меню по типу страницы (кнопки-ссылки на главной).</summary>
    public void NavigateTo(Type pageType) =>
        SelectedNav = NavItems.FirstOrDefault(n => n.PageType == pageType) ?? SelectedNav;

    public ObservableCollection<NavItem> NavItems { get; }
    public string UserName { get; }
    public string UserRole { get; }

    public string LicenseText
    {
        get
        {
            var s = _license.State;
            if (s.Payload is null) return "";
            return s.Payload.Type == LicenseType.Perpetual
                ? L.T("Бессрочная лицензия")
                : L.F("{0}: осталось {1} дн.", L.T(LicenseDefaults.TypeName(s.Payload.Type)), s.DaysLeft);
        }
    }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private PageViewModel? _currentPage;

    private bool _revertingNav;

    partial void OnSelectedNavChanged(NavItem? oldValue, NavItem? newValue)
    {
        if (_revertingNav || newValue is null) return;
        if (CurrentPage is { HasUnsavedChanges: true } && oldValue is not null
            && !_dialogs.Confirm(L.T("Документ не сохранён. Перейти в другой раздел? Введённые данные будут потеряны.")))
        {
            // Возврат выделения меню — после завершения текущего изменения (иначе ListBox его не примет).
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _revertingNav = true;
                SelectedNav = oldValue;
                _revertingNav = false;
            });
            return;
        }
        var value = newValue;
        var page = (PageViewModel)_services.GetRequiredService(value.PageType);
        CurrentPage = page;
        _ = page.LoadAsync();
    }
}
