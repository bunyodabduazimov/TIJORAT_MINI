using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiniTrade.Licensing;

/// <summary>
/// Формат токена: base64url(JSON) "." base64url(ECDSA P-256 / SHA-256 подпись).
/// Приватный ключ есть только на сервере, клиент содержит лишь публичный ключ для проверки.
/// </summary>
public static class LicenseTokenCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Sign(LicensePayload payload, ECDsa privateKey)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var signature = privateKey.SignData(body, HashAlgorithmName.SHA256);
        return $"{Base64Url.Encode(body)}.{Base64Url.Encode(signature)}";
    }

    public static bool TryVerify(string? token, ECDsa publicKey, out LicensePayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Trim().Split('.');
        if (parts.Length != 2) return false;
        try
        {
            var body = Base64Url.Decode(parts[0]);
            var signature = Base64Url.Decode(parts[1]);
            if (!publicKey.VerifyData(body, signature, HashAlgorithmName.SHA256)) return false;
            payload = JsonSerializer.Deserialize<LicensePayload>(body, Json);
            return payload is not null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
        {
            return false;
        }
    }

    public static ECDsa LoadPublicKey(string pem)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }
}

internal static class Base64Url
{
    public static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
        return Convert.FromBase64String(s);
    }
}
