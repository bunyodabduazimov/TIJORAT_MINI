using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.Licensing;

/// <summary>Уникальный идентификатор установки и отпечаток устройства.</summary>
public sealed class DeviceIdentity
{
    public string InstallationId { get; }
    public string Fingerprint { get; }
    public string MachineName => Environment.MachineName;

    public DeviceIdentity()
    {
        InstallationId = LoadOrCreateInstallationId();
        Fingerprint = ComputeFingerprint();
    }

    private static string LoadOrCreateInstallationId()
    {
        var path = Path.Combine(AppConfig.DataDirectory, "installation.id");
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var existing))
            return existing.ToString();
        Directory.CreateDirectory(AppConfig.DataDirectory);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(path, id);
        return id;
    }

    /// <summary>SHA-256 от MachineGuid Windows (стабилен в пределах установки ОС).</summary>
    private static string ComputeFingerprint()
    {
        string? machineGuid = null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            machineGuid = key?.GetValue("MachineGuid") as string;
        }
        catch (Exception) { /* нет доступа к реестру — используем запасной вариант */ }

        var source = $"MiniTrade|{machineGuid ?? Environment.MachineName + "|" + Environment.UserDomainName}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }
}
