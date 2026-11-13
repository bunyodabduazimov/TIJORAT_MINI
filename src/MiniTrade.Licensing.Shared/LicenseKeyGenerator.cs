using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MiniTrade.Licensing;

/// <summary>Генерация и нормализация ключей вида MT-XXXXX-XXXXX-XXXXX-XXXXX.</summary>
public static partial class LicenseKeyGenerator
{
    // Без похожих символов 0/O, 1/I/L
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string Generate()
    {
        var groups = Enumerable.Range(0, 4)
            .Select(_ => new string(Enumerable.Range(0, 5).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray()));
        return "MT-" + string.Join('-', groups);
    }

    public static string Normalize(string? key) => (key ?? "").Trim().ToUpperInvariant().Replace(' ', '-');

    public static bool IsWellFormed(string? key) => KeyRegex().IsMatch(Normalize(key));

    [GeneratedRegex("^MT(-[A-Z0-9]{5}){4}$")]
    private static partial Regex KeyRegex();
}
