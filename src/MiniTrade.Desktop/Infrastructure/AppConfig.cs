using System.IO;
using System.Text.Json;
using MiniTrade.Core.Data;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>
/// Настройки приложения из appsettings.json рядом с исполняемым файлом.
/// Установщик не перезаписывает этот файл при обновлении (там может быть свой путь к базе),
/// поэтому раздел «About» всегда берётся из копии, встроенной в программу.
/// </summary>
public sealed class AppConfig
{
    public string LicenseServerUrl { get; set; } = "https://lite.tijorat.pro";
    public string? DatabasePath { get; set; }
    /// <summary>Контакты и другие приложения для вкладки «О программе».</summary>
    public AboutSettings? About { get; set; }

    public string ResolvedDatabasePath => string.IsNullOrWhiteSpace(DatabasePath) ? TradeDatabase.DefaultPath : DatabasePath!;

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniTrade");

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static AppConfig Load()
    {
        var defaults = LoadDefaults();
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var local = File.Exists(path) ? TryParse(SafeRead(path)) : null;
        if (local is null) return defaults;

        if (string.IsNullOrWhiteSpace(local.LicenseServerUrl)) local.LicenseServerUrl = defaults.LicenseServerUrl;
        // Контакты разработчика — всегда из текущей версии программы (старый файл у клиента не обновляется)
        local.About = defaults.About ?? local.About;
        return local;
    }

    /// <summary>appsettings.json, встроенный в программу при сборке (актуальные контакты разработчика).</summary>
    private static AppConfig LoadDefaults()
    {
        try
        {
            using var stream = typeof(AppConfig).Assembly.GetManifestResourceStream("MiniTrade.DefaultSettings");
            if (stream is null) return new AppConfig();
            using var reader = new StreamReader(stream);
            return TryParse(reader.ReadToEnd()) ?? new AppConfig();
        }
        catch (Exception) { return new AppConfig(); }
    }

    private static string? SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception) { return null; }
    }

    private static AppConfig? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<AppConfig>(json, Options); }
        catch (JsonException) { return null; }
    }
}
