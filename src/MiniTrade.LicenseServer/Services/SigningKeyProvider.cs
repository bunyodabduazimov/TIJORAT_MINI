using System.Security.Cryptography;

namespace MiniTrade.LicenseServer.Services;

/// <summary>
/// Хранит приватный ключ ECDSA P-256 для подписи токенов. Ключ существует только на сервере.
/// Путь задаётся в Licensing:PrivateKeyPath; при отсутствии файла ключ генерируется автоматически.
/// </summary>
public sealed class SigningKeyProvider : IDisposable
{
    private readonly ECDsa _key;

    public SigningKeyProvider(IConfiguration config, IHostEnvironment env, ILogger<SigningKeyProvider> logger)
    {
        var path = config["Licensing:PrivateKeyPath"] ?? "keys/license-signing.pem";
        if (!Path.IsPathRooted(path)) path = Path.Combine(env.ContentRootPath, path);
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var pemFromEnv = config["Licensing:PrivateKeyPem"];
        if (!string.IsNullOrWhiteSpace(pemFromEnv))
        {
            _key.ImportFromPem(pemFromEnv);
        }
        else if (File.Exists(path))
        {
            _key.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _key.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "license-public.pem"), PublicKeyPem);
            logger.LogWarning("Сгенерирован новый ключ подписи лицензий: {Path}. Скопируйте license-public.pem в клиент (src/MiniTrade.Desktop/Resources).", path);
        }
    }

    public ECDsa Key => _key;

    public string PublicKeyPem => _key.ExportSubjectPublicKeyInfoPem();

    public void Dispose() => _key.Dispose();
}
