using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Сведения о доступном обновлении (GET api/v1/update на сайте лицензий).</summary>
public sealed record UpdateInfo(bool Success, string? Version, string? Url, long Size, string? Sha256);

/// <summary>Проверка, скачивание и запуск установщика новой версии.</summary>
public sealed class UpdateService(HttpClient http)
{
    /// <summary>Новая версия, если она на сервере старше текущей; иначе null (ошибки сети — тоже null).</summary>
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            var info = await http.GetFromJsonAsync<UpdateInfo>("api/v1/update", ct);
            if (info is not { Success: true, Version: { } v, Url: { } }) return null;
            if (!Version.TryParse(v, out var remote) || !Version.TryParse(currentVersion, out var local)) return null;
            return remote > local ? info : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Скачивает установщик во временную папку, проверяя SHA-256. progress — 0..1.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TijoratLiteUpdate");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"TijoratLite-Setup-{info.Version}.exe");

        using var response = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? info.Size;

        using var sha = SHA256.Create();
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                sha.TransformBlock(buffer, 0, n, null, 0);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }
        sha.TransformFinalBlock([], 0, 0);

        if (!string.IsNullOrEmpty(info.Sha256) &&
            !string.Equals(Convert.ToHexString(sha.Hash!), info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException(L.T("Файл обновления повреждён. Попробуйте ещё раз."));
        }
        return path;
    }

    /// <summary>Запускает тихую установку; установщик сам откроет программу после обновления.</summary>
    public static void RunInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
        {
            UseShellExecute = true, // нужен для запроса прав администратора (UAC)
        });
    }
}
