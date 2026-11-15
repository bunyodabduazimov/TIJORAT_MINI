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

    /// <summary>Контакты разработчика (панель справа).</summary>
    public AboutInfo About => AboutInfo.Current;

    // ── Язык интерфейса: меняется сразу, окно перерисовывается с новыми текстами ──
    public IReadOnlyList<LanguageInfo> Languages { get; } = L.Available();
    public bool HasLanguages => Languages.Count > 1;

    /// <summary>Сменился язык — окну нужно пересоздать содержимое.</summary>
    public event EventHandler? LanguageChanged;

    public LanguageInfo? Language
    {
        get => Languages.FirstOrDefault(l => l.Code == L.Current.Code);
        set
        {
            if (value is null || value.Code == L.Current.Code) return;
            App.Ui.Language = value.Code;
            App.Ui.Save();
            L.Load(value.Code);
            OnPropertyChanged();
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── Тема: переключение светлая ↔ тёмная, сохраняется в ui.json ──
    /// <summary>Глиф кнопки: в тёмной теме — солнце (перейти на светлую), в светлой — луна.</summary>
    public string ThemeGlyph => ThemeManager.IsDark ? "\U000F05A8" : "\U000F0594";

    [RelayCommand]
    private void ToggleTheme()
    {
        var theme = ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark;
        App.Ui.Theme = theme;
        App.Ui.Save();
        ThemeManager.Apply(theme);
        OnPropertyChanged(nameof(ThemeGlyph));
    }

    // ── Телефон: страна + номер по маске этой страны ──
    public IReadOnlyList<PhoneCountry> Countries => PhoneCountry.All;

    [ObservableProperty] private PhoneCountry _selectedCountry = PhoneCountry.Default;
    [ObservableProperty] private bool _isKeyMode;
    [ObservableProperty] private string _fullName = "";
    [ObservableProperty] private string _company = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _licenseKey = "";
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isError;

    /// <summary>Подсказка под полем телефона, пока номер введён не полностью.</summary>
    public string? PhoneHint => Phone.Length == 0 || SelectedCountry.IsComplete(Phone)
        ? null
        : L.F("Введите номер полностью: {0} {1}", SelectedCountry.Code, SelectedCountry.Placeholder);

    partial void OnPhoneChanged(string value) => OnPropertyChanged(nameof(PhoneHint));

    partial void OnSelectedCountryChanged(PhoneCountry? oldValue, PhoneCountry newValue)
    {
        // Цифры номера переносятся в маску новой страны (лишние обрезаются)
        var digits = oldValue?.ExtractDigits(Phone) ?? "";
        Phone = newValue.Format(newValue.ExtractDigits(digits));
        OnPropertyChanged(nameof(PhoneHint));
    }

    [RelayCommand]
    private Task RegisterTrialAsync() => ExecuteAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(FullName) || string.IsNullOrWhiteSpace(Phone))
            return new LicenseResponse(false, L.T("Заполните ФИО и телефон."));
        if (!SelectedCountry.IsComplete(Phone))
            return new LicenseResponse(false, L.F("Неверный номер телефона. Формат для страны «{0}»: {1} {2}",
                SelectedCountry.Name, SelectedCountry.Code, SelectedCountry.Placeholder));
        return await _license.RegisterTrialAsync(FullName.Trim(), Company.Trim(), SelectedCountry.FullNumber(Phone), SelectedCountry.Iso);
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
            ErrorLog.Write(ex, GetType().Name);
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
