using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Licensing;

namespace MiniTrade.Desktop.Licensing;

public enum OnlineCheckOutcome
{
    Refreshed,
    Revoked,
    Offline
}

/// <summary>
/// Управляет локальной лицензией: хранит подписанный сервером токен, проверяет его офлайн
/// (подпись, устройство, сроки, откат часов) и обновляет онлайн.
/// </summary>
public sealed class LicenseManager
{
    private readonly LicenseClient _client;
    private readonly DeviceIdentity _device;
    private readonly ECDsa _publicKey;
    private readonly string _statePath = Path.Combine(AppConfig.DataDirectory, "license.json");
    private LicenseFile _file;

    public LicenseManager(LicenseClient client, DeviceIdentity device)
    {
        _client = client;
        _device = device;
        _publicKey = LicenseTokenCodec.LoadPublicKey(ReadEmbeddedPublicKey());
        _file = LoadFile();
        State = Evaluate();
    }

    public LicenseState State { get; private set; }
    public DeviceIdentity Device => _device;
    public DateTime? LastOnlineCheckUtc => _file.LastOnlineCheckUtc;
    public string? StoredKey => State.Payload?.LicenseKey ?? _file.LastKey;

    public event EventHandler? StateChanged;

    public LicenseState Evaluate()
    {
        LicenseTokenCodec.TryVerify(_file.Token, _publicKey, out var payload);
        var state = _file.Token is not null && payload is null
            ? new LicenseState(LicenseCheckResult.InvalidSignature, null, L.T("Файл лицензии повреждён или изменён."))
            : LicenseEvaluator.Evaluate(payload, _device.InstallationId, _device.Fingerprint, DateTime.UtcNow, _file.LastSeenUtc);

        if (state.IsUsable && (_file.LastSeenUtc is null || DateTime.UtcNow > _file.LastSeenUtc))
        {
            _file.LastSeenUtc = DateTime.UtcNow;
            SaveFile();
        }
        return State = state;
    }

    public async Task<LicenseResponse> RegisterTrialAsync(string fullName, string company, string phone, CancellationToken ct = default)
    {
        var response = await _client.RegisterAsync(new RegisterRequest(DeviceInfo(), fullName, company, phone), ct);
        return Apply(response);
    }

    public async Task<LicenseResponse> ActivateAsync(string key, string? fullName, string? company, string? phone, CancellationToken ct = default)
    {
        var response = await _client.ActivateAsync(
            new ActivateRequest(DeviceInfo(), LicenseKeyGenerator.Normalize(key), fullName, company, phone), ct);
        return Apply(response);
    }

    /// <summary>Онлайн-проверка. Сетевые ошибки не отзывают лицензию — программа продолжает работать офлайн.</summary>
    public async Task<(OnlineCheckOutcome Outcome, string Message)> CheckOnlineAsync(CancellationToken ct = default)
    {
        var key = StoredKey;
        if (string.IsNullOrEmpty(key)) return (OnlineCheckOutcome.Offline, L.T("Лицензия не активирована."));
        try
        {
            var response = await _client.ValidateAsync(new ValidateRequest(DeviceInfo(), key), ct);
            if (response.Success)
            {
                Apply(response);
                return (OnlineCheckOutcome.Refreshed, response.Message);
            }
            if (response.ErrorCode is LicenseErrorCodes.Blocked or LicenseErrorCodes.Expired
                or LicenseErrorCodes.NotActivated or LicenseErrorCodes.KeyNotFound)
            {
                _file.Token = null;
                _file.LastOnlineCheckUtc = DateTime.UtcNow;
                SaveFile();
                Evaluate();
                StateChanged?.Invoke(this, EventArgs.Empty);
                return (OnlineCheckOutcome.Revoked, response.Message);
            }
            return (OnlineCheckOutcome.Offline, response.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return (OnlineCheckOutcome.Offline, L.T("Сервер лицензий недоступен. Программа работает офлайн."));
        }
    }

    private LicenseResponse Apply(LicenseResponse response)
    {
        if (!response.Success) return response;
        if (!LicenseTokenCodec.TryVerify(response.Token, _publicKey, out var payload) || payload is null)
            return response with { Success = false, Message = L.T("Ответ сервера не прошёл проверку подписи. Проверьте адрес сервера лицензий.") };

        _file.Token = response.Token;
        _file.LastKey = payload.LicenseKey;
        _file.LastOnlineCheckUtc = DateTime.UtcNow;
        _file.LastSeenUtc = DateTime.UtcNow;
        SaveFile();
        Evaluate();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return response;
    }

    private DeviceInfo DeviceInfo() => new(_device.InstallationId, _device.Fingerprint, _device.MachineName,
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    private static string ReadEmbeddedPublicKey()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MiniTrade.LicensePublicKey")
                           ?? throw new InvalidOperationException(L.T("Не найден публичный ключ лицензирования."));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private LicenseFile LoadFile()
    {
        try
        {
            if (File.Exists(_statePath))
                return JsonSerializer.Deserialize<LicenseFile>(File.ReadAllText(_statePath)) ?? new LicenseFile();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new LicenseFile();
    }

    private void SaveFile()
    {
        Directory.CreateDirectory(AppConfig.DataDirectory);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(_file, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class LicenseFile
    {
        public string? Token { get; set; }
        public string? LastKey { get; set; }
        public DateTime? LastSeenUtc { get; set; }
        public DateTime? LastOnlineCheckUtc { get; set; }
    }
}
