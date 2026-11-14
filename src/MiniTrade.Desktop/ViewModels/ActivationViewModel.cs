using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.Licensing;
using MiniTrade.Licensing;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Регистрация (пробный период 7 дней) и активация лицензионным ключом.</summary>
public partial class ActivationViewModel : ViewModelBase
{
    private readonly LicenseManager _license;
    private readonly AppConfig _config;

    public ActivationViewModel(LicenseManager license, AppConfig config, IDialogService dialogs) : base(dialogs)
    {
        _license = license;
        _config = config;
        LicenseKey = license.StoredKey ?? "";
        if (license.State.Result is not LicenseCheckResult.NoLicense)
            StatusMessage = license.State.Message;
    }

    public event EventHandler? Completed;

    public string InstallationId => _license.Device.InstallationId;
    public string ServerUrl => _config.LicenseServerUrl;
    public bool CanRetry => !string.IsNullOrEmpty(_license.StoredKey);

    [ObservableProperty] private bool _isKeyMode;
    [ObservableProperty] private string _fullName = "";
    [ObservableProperty] private string _company = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _licenseKey = "";
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isError;

    [RelayCommand]
    private Task RegisterTrialAsync() => ExecuteAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(FullName) || string.IsNullOrWhiteSpace(Phone))
            return new LicenseResponse(false, L.T("Заполните ФИО и телефон."));
        return await _license.RegisterTrialAsync(FullName.Trim(), Company.Trim(), Phone.Trim());
    });

    [RelayCommand]
    private Task ActivateAsync() => ExecuteAsync(async () =>
    {
        if (!LicenseKeyGenerator.IsWellFormed(LicenseKey))
            return new LicenseResponse(false, L.T("Введите ключ в формате MT-XXXXX-XXXXX-XXXXX-XXXXX."));
        return await _license.ActivateAsync(LicenseKey, null, null, null);
    });

    [RelayCommand]
    private Task RetryAsync() => ExecuteAsync(async () =>
    {
        var (outcome, message) = await _license.CheckOnlineAsync();
        return new LicenseResponse(outcome == OnlineCheckOutcome.Refreshed && _license.State.IsUsable, message);
    });

    private async Task ExecuteAsync(Func<Task<LicenseResponse>> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsError = false;
        StatusMessage = L.T("Подключение к серверу лицензий…");
        try
        {
            var result = await action();
            IsError = !result.Success;
            StatusMessage = result.Message;
            if (result.Success && _license.State.IsUsable)
            {
                Dialogs.Info($"{result.Message}\n\n{Describe(_license.State)}", L.T("Активация"));
                Completed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            IsError = true;
            StatusMessage = L.F("Не удалось подключиться к серверу лицензий ({0}). Проверьте интернет-соединение.", _config.LicenseServerUrl);
        }
        catch (Exception ex)
        {
            IsError = true;
            StatusMessage = L.F("Ошибка активации: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public static string Describe(LicenseState state)
    {
        if (state.Payload is not { } p) return state.Message;
        var term = p.ExpiresAtUtc is { } exp ? L.F("до {0:dd.MM.yyyy} (осталось {1} дн.)", exp.ToLocalTime(), state.DaysLeft) : L.T("бессрочно");
        return L.F("Лицензия: {0}, {1}", L.T(LicenseDefaults.TypeName(p.Type)), term);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
