using System.IO;
using Microsoft.Data.Sqlite;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Резервные копии базы SQLite: ручные, автоматические при выходе и восстановление.</summary>
public static class DatabaseBackup
{
    private const string AutoPrefix = "minitrade_auto_";

    public static string DefaultFolder => Path.Combine(AppConfig.DataDirectory, "backups");

    public static string FolderOf(UiSettings ui) => string.IsNullOrWhiteSpace(ui.BackupFolder) ? DefaultFolder : ui.BackupFolder!;

    /// <summary>Копия средствами SQLite Online Backup API (безопасно при открытой базе).</summary>
    public static void Copy(string sourcePath, string targetPath)
    {
        if (File.Exists(targetPath)) File.Delete(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Pooling=False");
        using var target = new SqliteConnection($"Data Source={targetPath};Pooling=False");
        source.Open();
        target.Open();
        source.BackupDatabase(target);
    }

    /// <summary>Автокопия в папку из настроек с удалением старых. Возвращает путь или null, если выключено.</summary>
    public static string? RunAuto(string dbPath, UiSettings ui)
    {
        if (!ui.AutoBackup || !File.Exists(dbPath)) return null;
        var folder = FolderOf(ui);
        var path = Path.Combine(folder, $"{AutoPrefix}{DateTime.Now:yyyyMMdd_HHmmss}.db");
        Copy(dbPath, path);
        foreach (var old in new DirectoryInfo(folder).GetFiles(AutoPrefix + "*.db")
                     .OrderByDescending(f => f.Name).Skip(Math.Max(1, ui.BackupKeep)))
        {
            try { old.Delete(); } catch (IOException) { }
        }
        ui.LastBackupAt = DateTime.Now;
        ui.Save();
        DeleteExpired(ui);
        return path;
    }

    /// <summary>Удаляет копии программы (minitrade_*.db / *.sql) старше заданного числа дней.</summary>
    public static void DeleteExpired(UiSettings ui)
    {
        if (ui.BackupKeepDays <= 0) return;
        var folder = FolderOf(ui);
        if (!Directory.Exists(folder)) return;
        var limit = DateTime.Now.AddDays(-ui.BackupKeepDays);
        var dir = new DirectoryInfo(folder);
        foreach (var f in dir.GetFiles("minitrade_*.db").Concat(dir.GetFiles("minitrade_*.sql")))
        {
            if (f.LastWriteTime >= limit) continue;
            try { f.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Проверка, что файл — база TIJORAT LITE.</summary>
    public static void Validate(string path)
    {
        try
        {
            using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('CompanySettings','Users','__EFMigrationsHistory')";
            if (Convert.ToInt32(cmd.ExecuteScalar()) != 3) throw new InvalidDataException();
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException)
        {
            throw new InvalidDataException(L.T("Файл не является резервной копией базы TIJORAT LITE."));
        }
    }

    /// <summary>
    /// Восстановление: текущая база сначала сохраняется в папку копий, затем заменяется содержимым файла.
    /// Возвращает путь к копии прежней базы. После вызова программу нужно перезапустить.
    /// </summary>
    public static string Restore(string backupPath, string dbPath, UiSettings ui)
    {
        if (string.Equals(Path.GetFullPath(backupPath), Path.GetFullPath(dbPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("Выбран файл текущей базы."));
        Validate(backupPath);
        var safety = Path.Combine(FolderOf(ui), $"minitrade_before_restore_{DateTime.Now:yyyyMMdd_HHmmss}.db");
        Copy(dbPath, safety);
        SqliteConnection.ClearAllPools(); // освободить соединения EF перед перезаписью
        using var source = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False");
        using var target = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        source.Open();
        target.Open();
        source.BackupDatabase(target);
        return safety;
    }
}
