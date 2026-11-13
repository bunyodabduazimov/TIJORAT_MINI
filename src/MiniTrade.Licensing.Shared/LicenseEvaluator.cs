namespace MiniTrade.Licensing;

public enum LicenseCheckResult
{
    Valid,
    /// <summary>Действует, но пора выполнить онлайн-проверку.</summary>
    ValidNeedsRefresh,
    NoLicense,
    InvalidSignature,
    WrongDevice,
    Expired,
    OfflinePeriodExceeded,
    ClockTampered
}

public sealed record LicenseState(LicenseCheckResult Result, LicensePayload? Payload, string Message)
{
    public bool IsUsable => Result is LicenseCheckResult.Valid or LicenseCheckResult.ValidNeedsRefresh;

    public int? DaysLeft => Payload?.ExpiresAtUtc is { } exp ? Math.Max(0, (int)Math.Ceiling((exp - DateTime.UtcNow).TotalDays)) : null;
}

/// <summary>Офлайн-проверка токена: подпись, привязка к устройству, сроки, откат часов.</summary>
public static class LicenseEvaluator
{
    /// <summary>За сколько дней до окончания офлайн-периода клиент начинает фоновое обновление.</summary>
    public const int RefreshAheadDays = 7;

    public static LicenseState Evaluate(LicensePayload? payload, string installationId, string fingerprint, DateTime nowUtc, DateTime? lastSeenUtc)
    {
        if (payload is null)
            return new(LicenseCheckResult.NoLicense, null, "Лицензия не найдена.");
        if (!string.Equals(payload.InstallationId, installationId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(payload.DeviceFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            return new(LicenseCheckResult.WrongDevice, payload, "Лицензия выдана для другого устройства.");
        // Допускаем погрешность часов 1 час
        if ((lastSeenUtc is { } last && nowUtc < last.AddHours(-1)) || nowUtc < payload.IssuedAtUtc.AddHours(-1))
            return new(LicenseCheckResult.ClockTampered, payload, "Обнаружено изменение системного времени. Требуется онлайн-проверка лицензии.");
        if (payload.ExpiresAtUtc is { } exp && nowUtc >= exp)
            return new(LicenseCheckResult.Expired, payload, $"Срок действия лицензии истёк {exp.ToLocalTime():dd.MM.yyyy}.");
        if (nowUtc >= payload.CheckBeforeUtc)
            return new(LicenseCheckResult.OfflinePeriodExceeded, payload, "Истёк срок работы без подключения к серверу. Подключитесь к интернету для проверки лицензии.");
        if (nowUtc >= payload.CheckBeforeUtc.AddDays(-RefreshAheadDays))
            return new(LicenseCheckResult.ValidNeedsRefresh, payload, "Лицензия действительна.");
        return new(LicenseCheckResult.Valid, payload, "Лицензия действительна.");
    }
}
