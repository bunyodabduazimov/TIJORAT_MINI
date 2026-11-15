using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>
/// Окно «Мой аккаунт» (щелчок по карточке пользователя в меню): данные пользователя и смена своего пароля.
/// Настройки интерфейса этого компьютера — в «Настройки → Компания» (<see cref="SettingsViewModel"/>).
/// </summary>
public partial class ProfileViewModel : ViewModelBase, ICloseRequest
{
    private readonly SettingsService _settings;
    private readonly UserSession _session;

    public ProfileViewModel(SettingsService settings, UserSession session, IDialogService dialogs) : base(dialogs)
    {
        _settings = settings;
        _session = session;
        var u = session.CurrentUser;
        FullName = u?.FullName ?? "";
        Login = u?.Login ?? "";
        Role = u is { IsAdmin: true } ? L.T("Администратор") : u?.AccessRole?.Name ?? "—";
        Phone = string.IsNullOrWhiteSpace(u?.Phone) ? "—" : u!.Phone!;
        LastLogin = u?.LastLoginAt?.ToString("dd.MM.yyyy HH:mm") ?? "—";
        Machine = Environment.MachineName;
    }

    public string Title => L.T("Мой аккаунт");
    public double DialogWidth => 640;
    public event EventHandler<bool>? CloseRequested;

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

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, false);
}
