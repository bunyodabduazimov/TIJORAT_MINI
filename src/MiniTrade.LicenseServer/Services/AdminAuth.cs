using System.Security.Cryptography;
using System.Text;

namespace MiniTrade.LicenseServer.Services;

/// <summary>
/// Учётная запись администратора панели: Admin:Username + Admin:PasswordHash (PBKDF2, см. --hash-password).
/// Для первого запуска допускается Admin:Password (в продакшене задавайте только хеш).
/// </summary>
public class AdminAuth(IConfiguration config)
{
    public bool Validate(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return false;
        var expectedUser = config["Admin:Username"] ?? "admin";
        var userOk = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(username), Encoding.UTF8.GetBytes(expectedUser));

        var hash = config["Admin:PasswordHash"];
        bool passOk;
        if (!string.IsNullOrWhiteSpace(hash))
            passOk = VerifyHash(password, hash);
        else
        {
            var plain = config["Admin:Password"];
            passOk = !string.IsNullOrEmpty(plain) &&
                     CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(plain));
        }
        return userOk && passOk;
    }

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(h)}";
    }

    private static bool VerifyHash(string password, string stored)
    {
        var p = stored.Split('$');
        if (p.Length != 4 || !int.TryParse(p[1], out var it)) return false;
        var expected = Convert.FromBase64String(p[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(p[2]), it, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public static class Fmt
{
    public static string Date(System.DateTime? utc) =>
        utc is null ? "—" : System.DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM.yyyy");

    public static string DateTime(System.DateTime? utc) =>
        utc is null ? "—" : System.DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm");
}
