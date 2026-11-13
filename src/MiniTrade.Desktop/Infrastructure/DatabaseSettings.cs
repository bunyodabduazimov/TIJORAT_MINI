using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiniTrade.Core.Data;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>
/// Выбор базы данных этого компьютера: %LocalAppData%\MiniTrade\database.dat, зашифрован Windows DPAPI
/// (в файле лежит пароль сервера). Нет файла — локальная SQLite.
/// </summary>
public static class DatabaseSettings
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TIJORAT MINI database");
    private static string FilePath => Path.Combine(AppConfig.DataDirectory, "database.dat");

    private sealed record Data(DatabaseProvider Provider, string Host, int Port, string Database, string Username, string Password);

    /// <summary>Подключение для запуска: сохранённый сервер или локальный файл из <see cref="AppConfig"/>.</summary>
    public static DatabaseConnection Load(AppConfig config)
    {
        var local = new DatabaseConnection { SqlitePath = config.ResolvedDatabasePath };
        try
        {
            if (!File.Exists(FilePath)) return local;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            // Параметры сервера храним и при выборе SQLite — чтобы не вводить их заново
            if (JsonSerializer.Deserialize<Data>(json) is not { } d) return local;
            return local with { Provider = d.Provider, Host = d.Host, Port = d.Port, Database = d.Database, Username = d.Username, Password = d.Password };
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
        {
            return local;
        }
    }

    public static void Save(DatabaseConnection c)
    {
        Directory.CreateDirectory(AppConfig.DataDirectory);
        var json = JsonSerializer.SerializeToUtf8Bytes(new Data(c.Provider, c.Host, c.Port, c.Database, c.Username, c.Password));
        File.WriteAllBytes(FilePath, ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
    }
}
