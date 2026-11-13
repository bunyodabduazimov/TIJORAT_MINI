using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>
/// «Запомнить пароль»: логин и пароль в %LocalAppData%\MiniTrade\login.dat, зашифрованные Windows DPAPI
/// (расшифровать может только этот пользователь Windows на этом компьютере).
/// </summary>
public static class SavedLogin
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TIJORAT MINI login");
    private static string FilePath => Path.Combine(AppConfig.DataDirectory, "login.dat");

    private sealed record Data(string Login, string Password);

    public static (string Login, string Password)? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Data>(json) is { } d ? (d.Login, d.Password) : null;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
        {
            return null; // файл повреждён или скопирован с другого компьютера — просто не подставляем
        }
    }

    public static void Save(string login, string password)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDirectory);
            var json = JsonSerializer.SerializeToUtf8Bytes(new Data(login, password));
            File.WriteAllBytes(FilePath, ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException) { }
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
