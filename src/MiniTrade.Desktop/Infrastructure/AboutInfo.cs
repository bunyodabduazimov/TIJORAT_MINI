using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Другое приложение разработчика (раздел «Наши приложения»).</summary>
public sealed class AppLink
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Url { get; set; }
    /// <summary>Логотип: http(s)-ссылка, абсолютный путь или путь относительно папки программы.</summary>
    public string? Logo { get; set; }

    private ImageSource? _logoImage;
    private bool _logoLoaded;

    /// <summary>Загруженный логотип (null — показать иконку по умолчанию).</summary>
    [JsonIgnore]
    public ImageSource? LogoImage
    {
        get
        {
            if (_logoLoaded) return _logoImage;
            _logoLoaded = true;
            if (string.IsNullOrWhiteSpace(Logo)) return null;
            try
            {
                var path = Logo.Trim();
                Uri uri = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(path)
                    ? new Uri(path, UriKind.Absolute)
                    : new Uri(Path.Combine(AppContext.BaseDirectory, path), UriKind.Absolute);
                if (uri.IsFile && !File.Exists(uri.LocalPath)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.DecodePixelWidth = 76;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                _logoImage = bmp;
            }
            catch (Exception) { _logoImage = null; }
            return _logoImage;
        }
    }
    [JsonIgnore] public bool HasLogo => LogoImage != null;
}

/// <summary>Контакты разработчика; переопределяются в appsettings.json (секция "About").</summary>
public sealed class AboutSettings
{
    public string? Website { get; set; } = "https://tijorat.pro";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Telegram { get; set; }
    public string? WhatsApp { get; set; }
    public string? Instagram { get; set; }
    public string? Facebook { get; set; }
    public string? TikTok { get; set; }
    /// <summary>Дополнительные номера телефонов.</summary>
    public List<string> Phones { get; set; } = [];
    public List<AppLink> Apps { get; set; } = [];
    /// <summary>Программы удалённого доступа (AnyDesk, RustDesk, TeamViewer…) — ссылки на скачивание.</summary>
    public List<AppLink> RemoteApps { get; set; } = [];
}

/// <summary>Данные вкладки «О программе»: версия, контакты, другие приложения.</summary>
public sealed class AboutInfo
{
    public static AboutInfo Current { get; } = new();

    private AboutInfo()
    {
        var c = AppConfig.Load().About ?? new AboutSettings();
        Website = c.Website; Phone = c.Phone; Email = c.Email; Telegram = c.Telegram; WhatsApp = c.WhatsApp;
        Instagram = c.Instagram; Facebook = c.Facebook; TikTok = c.TikTok;
        Phones = c.Phones.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        Apps = c.Apps.Where(a => !string.IsNullOrWhiteSpace(a.Name)).ToList();
        RemoteApps = c.RemoteApps.Where(a => !string.IsNullOrWhiteSpace(a.Name)).ToList();
    }

    public string ProductName => "TIJORAT.PRO";
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

    public string? Website { get; }
    public string? Phone { get; }
    public string? Email { get; }
    public string? Telegram { get; }
    public string? WhatsApp { get; }
    public string? Instagram { get; }
    public string? Facebook { get; }
    public string? TikTok { get; }
    public IReadOnlyList<string> Phones { get; }
    public string? WhatsAppLink => string.IsNullOrWhiteSpace(WhatsApp) ? null : "wa:" + WhatsApp;
    public IReadOnlyList<AppLink> Apps { get; }
    public bool HasApps => Apps.Count > 0;
    public IReadOnlyList<AppLink> RemoteApps { get; }
    public bool HasRemoteApps => RemoteApps.Count > 0;

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
