using System.IO;
using System.Text.Json;
using MiniTrade.Core.Data;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Настройки приложения из appsettings.json рядом с исполняемым файлом.</summary>
public sealed class AppConfig
{
    public string LicenseServerUrl { get; set; } = "https://api.tijorat.pro";
    public string? DatabasePath { get; set; }
    /// <summary>Контакты и другие приложения для вкладки «О программе».</summary>
    public AboutSettings? About { get; set; }

    public string ResolvedDatabasePath => string.IsNullOrWhiteSpace(DatabasePath) ? TradeDatabase.DefaultPath : DatabasePath!;

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniTrade");

    public static AppConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new AppConfig();
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new AppConfig();
        }
        catch (JsonException)
        {
            return new AppConfig();
        }
    }
}
