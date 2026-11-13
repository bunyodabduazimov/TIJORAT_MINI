using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Другое приложение разработчика (раздел «Наши приложения»).</summary>
public sealed class AppLink
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Url { get; set; }
}

/// <summary>Контакты разработчика; переопределяются в appsettings.json (секция "About").</summary>
public sealed class AboutSettings
{
    public string? Website { get; set; } = "https://tijorat.pro";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Telegram { get; set; }
    public string? WhatsApp { get; set; }
    public List<AppLink> Apps { get; set; } = [];
}

/// <summary>Данные вкладки «О программе»: версия, контакты, другие приложения.</summary>
public sealed class AboutInfo
{
    public static AboutInfo Current { get; } = new();

    private AboutInfo()
    {
        var c = AppConfig.Load().About ?? new AboutSettings();
        Website = c.Website; Phone = c.Phone; Email = c.Email; Telegram = c.Telegram; WhatsApp = c.WhatsApp;
        Apps = c.Apps.Where(a => !string.IsNullOrWhiteSpace(a.Name)).ToList();
    }

    public string ProductName => "TIJORAT MINI";
    public string Version
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(AboutInfo).Assembly;
            var v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? asm.GetName().Version?.ToString() ?? "";
            var plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }
    public string Runtime => $".NET {Environment.Version} · Windows {Environment.OSVersion.Version}";
    public string Copyright => $"© {DateTime.Now.Year} Tijorat";

    public string? Website { get; }
    public string? Phone { get; }
    public string? Email { get; }
    public string? Telegram { get; }
    public string? WhatsApp { get; }
    public string? WhatsAppLink => string.IsNullOrWhiteSpace(WhatsApp) ? null : "wa:" + WhatsApp;
    public IReadOnlyList<AppLink> Apps { get; }
    public bool HasApps => Apps.Count > 0;

    /// <summary>Открыть ссылку: сайт, mailto:, tel:, Telegram (@ник или ссылка), WhatsApp (номер).</summary>
    public ICommand OpenCommand { get; } = new RelayCommand<string>(Open);

    private static void Open(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        target = target.Trim();
        var url = target switch
        {
            _ when target.StartsWith("http", StringComparison.OrdinalIgnoreCase) => target,
            _ when target.StartsWith("mailto:") || target.StartsWith("tel:") => target,
            _ when target.StartsWith('@') => "https://t.me/" + target[1..],
            _ when target.Contains('@') => "mailto:" + target,
            _ when target.StartsWith("wa:") => "https://wa.me/" + new string(target[3..].Where(char.IsDigit).ToArray()),
            _ => "tel:" + new string(target.Where(ch => char.IsDigit(ch) || ch == '+').ToArray())
        };
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { /* нет программы для ссылки — ничего не делаем */ }
    }
}
