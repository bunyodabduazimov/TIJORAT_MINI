using System.Text.Json.Serialization;

namespace MiniTrade.Licensing;

public enum LicenseType
{
    /// <summary>Пробный период (7 дней).</summary>
    Trial = 1,
    /// <summary>Годовая лицензия.</summary>
    Annual = 2,
    /// <summary>Бессрочная лицензия.</summary>
    Perpetual = 3
}

public enum LicenseStatus
{
    Active = 1,
    Blocked = 2
}

public static class LicenseDefaults
{
    public const int TrialDays = 7;
    public const int AnnualDays = 365;
    /// <summary>Сколько дней токен действует офлайн без повторной онлайн-проверки.</summary>
    public const int OfflineDays = 30;
    public const int DefaultMaxDevices = 1;

    public static string TypeName(LicenseType type) => type switch
    {
        LicenseType.Trial => "Пробная",
        LicenseType.Annual => "Годовая",
        LicenseType.Perpetual => "Бессрочная",
        _ => type.ToString()
    };
}

/// <summary>Содержимое подписанного токена лицензии, хранимого на клиенте.</summary>
public sealed record LicensePayload
{
    [JsonPropertyName("key")] public string LicenseKey { get; init; } = "";
    [JsonPropertyName("type")] public LicenseType Type { get; init; }
    [JsonPropertyName("customer")] public string CustomerName { get; init; } = "";
    [JsonPropertyName("company")] public string Company { get; init; } = "";
    [JsonPropertyName("iid")] public string InstallationId { get; init; } = "";
    [JsonPropertyName("dfp")] public string DeviceFingerprint { get; init; } = "";
    [JsonPropertyName("iat")] public DateTime IssuedAtUtc { get; init; }
    /// <summary>Окончание лицензии; null — бессрочная.</summary>
    [JsonPropertyName("exp")] public DateTime? ExpiresAtUtc { get; init; }
    /// <summary>До какого момента токен действует без связи с сервером.</summary>
    [JsonPropertyName("chk")] public DateTime CheckBeforeUtc { get; init; }
}

// ===== DTO API лицензирования =====

public sealed record DeviceInfo(string InstallationId, string DeviceFingerprint, string MachineName, string AppVersion);

public sealed record RegisterRequest(DeviceInfo Device, string FullName, string Company, string Phone);

public sealed record ActivateRequest(DeviceInfo Device, string LicenseKey, string? FullName = null, string? Company = null, string? Phone = null);

public sealed record ValidateRequest(DeviceInfo Device, string LicenseKey);

public sealed record LicenseResponse(bool Success, string Message, string? Token = null, string? ErrorCode = null);

public static class LicenseErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string KeyNotFound = "key_not_found";
    public const string Blocked = "blocked";
    public const string Expired = "expired";
    public const string DeviceLimit = "device_limit";
    public const string TrialUsed = "trial_used";
    public const string NotActivated = "not_activated";
}
