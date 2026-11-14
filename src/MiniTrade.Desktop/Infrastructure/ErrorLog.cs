using System.IO;
using System.Text;
using MiniTrade.Core.Common;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>
/// Журнал ошибок в текстовых файлах: на каждый день отдельный файл
/// %LocalAppData%\MiniTrade\logs\errors-ГГГГ-ММ-ДД.txt. Старые файлы удаляются через <see cref="KeepDays"/> дней.
/// Бизнес-ошибки (проверки ввода) не пишутся — только непредвиденные.
/// </summary>
public static class ErrorLog
{
    public const int KeepDays = 60;
    private static readonly object Sync = new();
    private static DateTime _cleanedOn;

    public static string Folder => Path.Combine(AppConfig.DataDirectory, "logs");

    public static string TodayFile => FileFor(DateTime.Today);

    private static string FileFor(DateTime day) => Path.Combine(Folder, $"errors-{day:yyyy-MM-dd}.txt");

    /// <summary>Записывает исключение с местом возникновения (контекстом) и полным стеком.</summary>
    public static void Write(Exception ex, string? context = null)
    {
        if (ex is BusinessException or OperationCanceledException) return;
        var sb = new StringBuilder();
        sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ОШИБКА");
        if (!string.IsNullOrWhiteSpace(context)) sb.Append(" (").Append(context).Append(')');
        sb.AppendLine();
        sb.AppendLine(ex.ToString()); // тип, сообщение, вложенные исключения и стек
        sb.AppendLine(new string('-', 100));
        Append(sb.ToString());
    }

    /// <summary>Записывает текстовое сообщение об ошибке (без исключения).</summary>
    public static void Write(string message, string? context = null)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ОШИБКА{(string.IsNullOrWhiteSpace(context) ? "" : $" ({context})")}: {message}"
                   + Environment.NewLine + new string('-', 100) + Environment.NewLine;
        Append(line);
    }

    private static void Append(string text)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(TodayFile, text, Encoding.UTF8);
                CleanupOld();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // журнал не должен ломать программу
        }
    }

    private static void CleanupOld()
    {
        if (_cleanedOn == DateTime.Today) return;
        _cleanedOn = DateTime.Today;
        var border = DateTime.Today.AddDays(-KeepDays);
        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("errors-*.txt"))
        {
            if (file.LastWriteTime >= border) continue;
            try { file.Delete(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
