using Microsoft.EntityFrameworkCore;
using MiniTrade.LicenseServer.Data;
using MiniTrade.Licensing;

namespace MiniTrade.LicenseServer.Services;

/// <summary>Бизнес-логика регистрации, активации и проверки лицензий.</summary>
public class LicenseService(LicenseDbContext db, SigningKeyProvider keys, TimeProvider time)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<LicenseResponse> RegisterTrialAsync(RegisterRequest req, string? ip, CancellationToken ct)
    {
        if (!IsValidDevice(req.Device) || string.IsNullOrWhiteSpace(req.FullName) || string.IsNullOrWhiteSpace(req.Phone))
            return Fail("Заполните ФИО и телефон.", LicenseErrorCodes.InvalidRequest);

        var device = await UpsertDeviceAsync(req.Device, ip, ct);

        // Пробный период выдаётся одному устройству (по отпечатку оборудования) только один раз
        var existingTrial = await db.Activations.Include(a => a.License).ThenInclude(l => l!.Customer)
            .Where(a => a.License!.Type == LicenseType.Trial && a.Device!.Fingerprint == device.Fingerprint)
            .OrderByDescending(a => a.Id).FirstOrDefaultAsync(ct);
        if (existingTrial is not null)
        {
            var lic = existingTrial.License!;
            if (lic.Status == LicenseStatus.Blocked) return Fail("Лицензия заблокирована администратором.", LicenseErrorCodes.Blocked);
            if (lic.IsExpired(Now))
                return Fail("Пробный период на этом устройстве уже использован. Введите лицензионный ключ.", LicenseErrorCodes.TrialUsed);
            // Переустановка в течение пробного периода — продолжаем тот же триал
            await BindAsync(lic, device, ct);
            await LogAsync("trial_resumed", lic.Id, device.Id, null, ip, ct);
            await db.SaveChangesAsync(ct);
            return Ok("Пробный период продолжен.", lic, device);
        }

        var customer = new Customer { FullName = req.FullName.Trim(), Company = req.Company?.Trim() ?? "", Phone = req.Phone.Trim() };
        var license = new License
        {
            Key = await NewUniqueKeyAsync(ct),
            Customer = customer,
            Type = LicenseType.Trial,
            ExpiresAt = Now.AddDays(LicenseDefaults.TrialDays),
            MaxDevices = 1,
            Notes = "Создана автоматически при регистрации"
        };
        db.Licenses.Add(license);
        license.Activations.Add(new Activation { Device = device, ActivatedAt = Now, LastCheckAt = Now });
        await db.SaveChangesAsync(ct);
        await LogAsync("trial_registered", license.Id, device.Id, $"{customer.FullName}, {customer.Company}, {customer.Phone}", ip, ct);
        await db.SaveChangesAsync(ct);
        return Ok($"Пробный период активирован на {LicenseDefaults.TrialDays} дней.", license, device);
    }

    public async Task<LicenseResponse> ActivateAsync(ActivateRequest req, string? ip, CancellationToken ct)
    {
        if (!IsValidDevice(req.Device) || !LicenseKeyGenerator.IsWellFormed(req.LicenseKey))
            return Fail("Неверный формат лицензионного ключа.", LicenseErrorCodes.InvalidRequest);

        var key = LicenseKeyGenerator.Normalize(req.LicenseKey);
        var license = await db.Licenses.Include(l => l.Customer).Include(l => l.Activations)
            .FirstOrDefaultAsync(l => l.Key == key, ct);
        if (license is null)
        {
            await LogAsync("activation_failed", null, null, $"Ключ не найден: {key}", ip, ct);
            await db.SaveChangesAsync(ct);
            return Fail("Лицензионный ключ не найден.", LicenseErrorCodes.KeyNotFound);
        }
        if (license.Status == LicenseStatus.Blocked) return Fail("Лицензия заблокирована. Обратитесь к поставщику.", LicenseErrorCodes.Blocked);
        if (license.IsExpired(Now)) return Fail("Срок действия лицензии истёк.", LicenseErrorCodes.Expired);

        var device = await UpsertDeviceAsync(req.Device, ip, ct);
        var already = license.Activations.Any(a => a.IsActive && a.DeviceId == device.Id);
        if (!already && license.Activations.Count(a => a.IsActive) >= license.MaxDevices)
        {
            await LogAsync("activation_denied", license.Id, device.Id, "Превышен лимит устройств", ip, ct);
            await db.SaveChangesAsync(ct);
            return Fail($"Ключ уже активирован на максимальном числе устройств ({license.MaxDevices}).", LicenseErrorCodes.DeviceLimit);
        }

        // Заполняем данные клиента, если ключ выдан без них
        var c = license.Customer!;
        if (string.IsNullOrWhiteSpace(c.Phone) && !string.IsNullOrWhiteSpace(req.Phone)) c.Phone = req.Phone.Trim();
        if (string.IsNullOrWhiteSpace(c.Company) && !string.IsNullOrWhiteSpace(req.Company)) c.Company = req.Company.Trim();

        // Устройство переходит на новую лицензию — прежние активации (например, триал) закрываем
        var others = await db.Activations.Where(a => a.DeviceId == device.Id && a.IsActive && a.LicenseId != license.Id).ToListAsync(ct);
        foreach (var o in others) { o.IsActive = false; o.DeactivatedAt = Now; }

        await BindAsync(license, device, ct);
        await LogAsync("activated", license.Id, device.Id, device.MachineName, ip, ct);
        await db.SaveChangesAsync(ct);
        return Ok("Лицензия успешно активирована.", license, device);
    }

    public async Task<LicenseResponse> ValidateAsync(ValidateRequest req, string? ip, CancellationToken ct)
    {
        if (!IsValidDevice(req.Device)) return Fail("Некорректный запрос.", LicenseErrorCodes.InvalidRequest);
        var key = LicenseKeyGenerator.Normalize(req.LicenseKey);
        var license = await db.Licenses.Include(l => l.Customer).FirstOrDefaultAsync(l => l.Key == key, ct);
        if (license is null) return Fail("Лицензия не найдена.", LicenseErrorCodes.KeyNotFound);

        var device = await db.Devices.FirstOrDefaultAsync(d => d.InstallationId == req.Device.InstallationId, ct);
        if (device is null || !string.Equals(device.Fingerprint, req.Device.DeviceFingerprint, StringComparison.OrdinalIgnoreCase))
            return Fail("Устройство не активировано.", LicenseErrorCodes.NotActivated);

        var activation = await db.Activations.FirstOrDefaultAsync(a => a.LicenseId == license.Id && a.DeviceId == device.Id && a.IsActive, ct);
        if (activation is null) return Fail("Активация отозвана администратором.", LicenseErrorCodes.NotActivated);
        if (license.Status == LicenseStatus.Blocked)
            return Fail($"Лицензия заблокирована. {license.BlockReason}".Trim(), LicenseErrorCodes.Blocked);
        if (license.IsExpired(Now)) return Fail("Срок действия лицензии истёк.", LicenseErrorCodes.Expired);

        activation.LastCheckAt = Now;
        device.LastSeenAt = Now;
        device.LastIp = ip;
        device.AppVersion = req.Device.AppVersion;
        await db.SaveChangesAsync(ct);
        return Ok("Лицензия действительна.", license, device);
    }

    // ===== Вспомогательное =====

    private async Task BindAsync(License license, Device device, CancellationToken ct)
    {
        var act = await db.Activations.FirstOrDefaultAsync(a => a.LicenseId == license.Id && a.DeviceId == device.Id && a.IsActive, ct);
        if (act is null)
            db.Activations.Add(new Activation { LicenseId = license.Id, DeviceId = device.Id, ActivatedAt = Now, LastCheckAt = Now });
        else
            act.LastCheckAt = Now;
    }

    private async Task<Device> UpsertDeviceAsync(DeviceInfo info, string? ip, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.InstallationId == info.InstallationId, ct);
        if (device is null)
        {
            device = new Device { InstallationId = info.InstallationId, FirstSeenAt = Now };
            db.Devices.Add(device);
        }
        device.Fingerprint = info.DeviceFingerprint;
        device.MachineName = Truncate(info.MachineName, 200);
        device.AppVersion = Truncate(info.AppVersion, 32);
        device.LastSeenAt = Now;
        device.LastIp = ip;
        await db.SaveChangesAsync(ct);
        return device;
    }

    public async Task<string> NewUniqueKeyAsync(CancellationToken ct)
    {
        while (true)
        {
            var key = LicenseKeyGenerator.Generate();
            if (!await db.Licenses.AnyAsync(l => l.Key == key, ct)) return key;
        }
    }

    private LicenseResponse Ok(string message, License license, Device device)
    {
        var payload = new LicensePayload
        {
            LicenseKey = license.Key,
            Type = license.Type,
            CustomerName = license.Customer?.FullName ?? "",
            Company = license.Customer?.Company ?? "",
            InstallationId = device.InstallationId,
            DeviceFingerprint = device.Fingerprint,
            IssuedAtUtc = Now,
            ExpiresAtUtc = license.ExpiresAt is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null,
            CheckBeforeUtc = Now.AddDays(LicenseDefaults.OfflineDays)
        };
        return new LicenseResponse(true, message, LicenseTokenCodec.Sign(payload, keys.Key));
    }

    private static LicenseResponse Fail(string message, string code) => new(false, message, null, code);

    private Task LogAsync(string action, int? licenseId, int? deviceId, string? details, string? ip, CancellationToken ct)
    {
        db.Events.Add(new LicenseEvent { At = Now, Action = action, LicenseId = licenseId, DeviceId = deviceId, Details = details, Ip = ip });
        return Task.CompletedTask;
    }

    public Task AdminLogAsync(string action, int? licenseId, int? deviceId, string? details, CancellationToken ct = default) =>
        LogAsync(action, licenseId, deviceId, details, "admin", ct);

    private static bool IsValidDevice(DeviceInfo? d) =>
        d is not null
        && Guid.TryParse(d.InstallationId, out _)
        && !string.IsNullOrWhiteSpace(d.DeviceFingerprint) && d.DeviceFingerprint.Length is >= 16 and <= 128;

    private static string Truncate(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max];
}
