using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>
/// Окно «Мой аккаунт» (щелчок по карточке пользователя в меню): данные пользователя, смена своего пароля
/// и настройки интерфейса этого компьютера — тема, язык, масштаб, стартовая страница, меню.
/// </summary>
public partial class ProfileViewModel : ViewModelBase, ICloseRequest
{
    public const int AccountTab = 0;
    public const int InterfaceTab = 1;

    private readonly SettingsService _settings;
    private readonly UserSession _session;
    private readonly bool _ready;

    public ProfileViewModel(SettingsService settings, UserSession session, IDialogService dialogs, int tab = AccountTab) : base(dialogs)
    {
        _settings = settings;
        _session = session;
        _selectedTab = tab;
        var u = session.CurrentUser;
        FullName = u?.FullName ?? "";
        Login = u?.Login ?? "";
        Role = u is { IsAdmin: true } ? L.T("Администратор") : u?.AccessRole?.Name ?? "—";
        Phone = string.IsNullOrWhiteSpace(u?.Phone) ? "—" : u!.Phone!;
        LastLogin = u?.LastLoginAt?.ToString("dd.MM.yyyy HH:mm") ?? "—";
        Machine = Environment.MachineName;

        _theme = App.Ui.Theme;
        _language = Languages.FirstOrDefault(l => l.Code == L.Current.Code);
        _scale = Scales.OrderBy(s => Math.Abs(s.Value - App.Ui.Scale)).First();
        _startPage = StartPages.FirstOrDefault(p => p.Value == App.Ui.StartPage) ?? StartPages[0];
        _menuCollapsed = App.Ui.MenuCollapsed;
        _ready = true;
    }

    public string Title => L.T("Мой аккаунт");
    public double DialogWidth => 640;
    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty] private int _selectedTab;

    // ── Профиль ──
    public string FullName { get; }
    public string Login { get; }
    public string Role { get; }
    public string Phone { get; }
    public string LastLogin { get; }
    public string Machine { get; }
    public string Initial => string.IsNullOrWhiteSpace(FullName) ? "?" : FullName.Trim()[..1].ToUpperInvariant();

    // ── Смена пароля ──
    [ObservableProperty] private string? _oldPassword;
    [ObservableProperty] private string? _newPassword;
    [ObservableProperty] private string? _newPasswordRepeat;

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        if (NewPassword != NewPasswordRepeat)
        {
            Dialogs.Error(L.T("Новый пароль и повтор не совпадают."));
            return;
        }
        if (!await RunAsync(() => _settings.ChangeOwnPasswordAsync(OldPassword, NewPassword))) return;
        // Сохранённый вход «Запомнить меня» — с новым паролем
        if (SavedLogin.Load() is { } saved && _session.CurrentUser is { } me
            && string.Equals(saved.Login, me.Login, StringComparison.OrdinalIgnoreCase))
            SavedLogin.Save(saved.Login, NewPassword!);
        OldPassword = NewPassword = NewPasswordRepeat = null;
        Dialogs.Info(L.T("Пароль изменён."));
    }

    // ── Интерфейс (настройки этого компьютера) ──
    [ObservableProperty] private AppTheme _theme;
    public IReadOnlyList<LanguageInfo> Languages { get; } = L.Available();
    [ObservableProperty] private LanguageInfo? _language;

    public IReadOnlyList<Choice<double>> Scales { get; } =
        [new(0.8, "80%"), new(0.9, "90%"), new(1.0, "100%"), new(1.1, "110%"), new(1.25, "125%"), new(1.5, "150%")];
    [ObservableProperty] private Choice<double>? _scale;

    public IReadOnlyList<Choice<string>> StartPages { get; } =
    [
        new(nameof(DashboardViewModel), L.T("Главная")),
        new(nameof(FinanceViewModel), L.T("Финансы")),
        new(nameof(SalesViewModel), L.T("Продажи")),
        new(nameof(PurchasesViewModel), L.T("Закуп")),
        new(nameof(WarehouseViewModel), L.T("Склад")),
        new(nameof(CatalogViewModel), L.T("Справочники")),
        new(nameof(ReportsViewModel), L.T("Отчёты")),
    ];
    [ObservableProperty] private Choice<string>? _startPage;
    [ObservableProperty] private bool _menuCollapsed;

    partial void OnThemeChanged(AppTheme value)
    {
        if (!_ready) return;
        App.Ui.Theme = value;
        App.Ui.Save();
        ThemeManager.Apply(value);
    }

    partial void OnScaleChanged(Choice<double>? value)
    {
        if (!_ready || value is null || Math.Abs(value.Value - App.Ui.Scale) < 0.001) return;
        App.Ui.Scale = value.Value;
        App.Ui.Save();
        UiSettings.RaiseScaleChanged();
    }

    partial void OnStartPageChanged(Choice<string>? value)
    {
        if (!_ready || value is null) return;
        App.Ui.StartPage = value.Value;
        App.Ui.Save();
    }

    partial void OnMenuCollapsedChanged(bool value)
    {
        if (!_ready) return;
        App.Ui.MenuCollapsed = value;
        App.Ui.Save();
        if (System.Windows.Application.Current.MainWindow?.DataContext is MainViewModel main)
            main.IsMenuCollapsed = value;
    }

    /// <summary>Язык: окно закрывается, главное окно пересоздаётся с новыми текстами и снова открывает эту вкладку.</summary>
    partial void OnLanguageChanged(LanguageInfo? value)
    {
        if (!_ready || value is null || value.Code == L.Current.Code) return;
        App.Ui.Language = value.Code;
        App.Ui.Save();
        L.Load(value.Code);
        CloseRequested?.Invoke(this, false);
        System.Windows.Application.Current.Dispatcher.BeginInvoke(MainViewModel.ReloadWithProfile,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, false);
}
