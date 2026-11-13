using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniTrade.Desktop.Infrastructure;

public enum AppTheme { System, Light, Dark }

/// <summary>Настройки интерфейса пользователя этого компьютера: %LocalAppData%\MiniTrade\ui.json.</summary>
public sealed class UiSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Код языка: ru, en … (файлы Languages/*.json).</summary>
    public string Language { get; set; } = L.DefaultLanguage;

    /// <summary>Боковое меню свёрнуто до иконок.</summary>
    public bool MenuCollapsed { get; set; }

    /// <summary>Масштаб интерфейса: 0.8 … 1.5 (1 — 100%).</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Страница, открываемая после входа (имя типа ViewModel; пусто — «Главная»).</summary>
    public string? StartPage { get; set; }

    /// <summary>Сообщает открытым окнам, что масштаб изменился.</summary>
    public static event Action? ScaleChanged;
    public static void RaiseScaleChanged() => ScaleChanged?.Invoke();

    /// <summary>Ширина колонки чека в «Продажах» (запоминается после перетаскивания разделителя).</summary>
    public double? SalesCartWidth { get; set; }

    // Печать
    /// <summary>Принтер для чеков и ордеров (пусто — принтер Windows по умолчанию).</summary>
    public string? PrinterName { get; set; }
    /// <summary>Печатать сразу, без окна выбора принтера.</summary>
    public bool PrintWithoutDialog { get; set; }
    /// <summary>Вид чека, накладной и этикеток, предпросмотр, принтер этикеток.</summary>
    public Printing.PrintOptions Print { get; set; } = new();

    // Резервное копирование
    /// <summary>Автоматическая копия базы при выходе из программы.</summary>
    public bool AutoBackup { get; set; } = true;
    /// <summary>Папка автокопий (пусто — %LocalAppData%\MiniTrade\backups).</summary>
    public string? BackupFolder { get; set; }
    /// <summary>Сколько последних автокопий хранить.</summary>
    public int BackupKeep { get; set; } = 10;
    public DateTime? LastBackupAt { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string FilePath => Path.Combine(AppConfig.DataDirectory, "ui.json");

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), Json) ?? new UiSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new UiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
