using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Data;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

public partial class LoginViewModel(SettingsService settings, IDialogService dialogs) : ViewModelBase(dialogs)
{
    public event EventHandler? LoggedIn;

    [ObservableProperty] private string _login = SavedLogin.Load()?.Login ?? DbInitializer.DefaultAdminLogin;

    /// <summary>Запомнить логин и пароль на этом компьютере (Infrastructure/SavedLogin).</summary>
    [ObservableProperty] private bool _rememberPassword = SavedLogin.Load() is not null;

    /// <summary>Сохранённый пароль — окно подставляет его в поле пароля при открытии.</summary>
    public string? SavedPassword { get; } = SavedLogin.Load()?.Password;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>Пароль передаётся из PasswordBox (не хранится в ViewModel).</summary>
    [RelayCommand]
    private async Task SignInAsync(string? password)
    {
        ErrorMessage = null;
        await RunAsync(async () =>
        {
            var user = await settings.AuthenticateAsync(Login ?? "", password ?? "");
            if (user is null) ErrorMessage = L.T("Неверный логин или пароль.");
            else
            {
                if (RememberPassword) SavedLogin.Save(Login ?? "", password ?? "");
                else SavedLogin.Clear();
                LoggedIn?.Invoke(this, EventArgs.Empty);
            }
        });
    }
}
