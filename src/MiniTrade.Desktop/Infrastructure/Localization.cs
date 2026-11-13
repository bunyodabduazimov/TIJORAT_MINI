using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Язык интерфейса из списка выбора.</summary>
public sealed record LanguageInfo(string Code, string Name, string Culture)
{
    public override string ToString() => Name;
}

/// <summary>
/// Локализация интерфейса.
/// Исходный язык — русский: русский текст сам является ключом (<c>L.T("Сохранить")</c>, в XAML <c>{l:T 'Сохранить'}</c>).
/// Переводы — файлы <c>Languages/&lt;код&gt;.json</c> рядом с программой:
/// <code>
/// { "language": { "name": "English", "culture": "en-GB" },
///   "strings":  { "Сохранить": "Save", "Продажа {0} проведена.": "Sale {0} posted." } }
/// </code>
/// Чтобы добавить язык, скопируйте en.json в, например, tg.json и переведите значения — перекомпиляция не нужна.
/// Если перевода нет, показывается русский текст.
/// </summary>
public static class L
{
    public const string DefaultLanguage = "ru";
    private static readonly LanguageInfo Russian = new("ru", "Русский", "ru-RU");
    private static Dictionary<string, string> _strings = new();

    public static LanguageInfo Current { get; private set; } = Russian;

    /// <summary>Культура для дат и календаря (ru-RU, en-GB …).</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(Russian.Culture);

    public static string LanguagesDirectory => Path.Combine(AppContext.BaseDirectory, "Languages");

    /// <summary>Русский (встроенный) + все корректные файлы Languages/*.json.</summary>
    public static IReadOnlyList<LanguageInfo> Available()
    {
        var list = new List<LanguageInfo> { Russian };
        if (Directory.Exists(LanguagesDirectory))
        {
            foreach (var file in Directory.GetFiles(LanguagesDirectory, "*.json").OrderBy(f => f))
            {
                var code = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (code == DefaultLanguage || list.Any(l => l.Code == code)) continue;
                if (TryRead(file, out var info, out _)) list.Add(info with { Code = code });
            }
        }
        return list;
    }

    /// <summary>Включает язык; неизвестный или повреждённый файл — остаётся русский.</summary>
    public static void Load(string? code)
    {
        code = string.IsNullOrWhiteSpace(code) ? DefaultLanguage : code.Trim().ToLowerInvariant();
        var info = Russian;
        var strings = new Dictionary<string, string>();
        if (code != DefaultLanguage && TryRead(Path.Combine(LanguagesDirectory, code + ".json"), out var read, out var map))
        {
            info = read with { Code = code };
            strings = map;
        }

        _strings = strings;
        Current = info;
        try { Culture = CultureInfo.GetCultureInfo(info.Culture); }
        catch (CultureNotFoundException) { Culture = CultureInfo.GetCultureInfo(Russian.Culture); }
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;

        // Окна берут язык (формат дат, названия месяцев в календаре) из этого ресурса — см. стиль AppWindow
        if (Application.Current is { } app)
            app.Resources["AppXmlLanguage"] = XmlLanguage.GetLanguage(Culture.IetfLanguageTag);
    }

    /// <summary>Перевод русской строки на текущий язык.</summary>
    public static string T(string russian)
    {
        if (string.IsNullOrEmpty(russian) || _strings.Count == 0) return russian;
        if (_strings.TryGetValue(russian, out var s) && !string.IsNullOrEmpty(s)) return s;
#if DEBUG
        if (russian.Any(c => c is >= (char)0x0400 and <= (char)0x04FF)) Missing.Add(russian);
#endif
        return russian;
    }

    /// <summary>Перевод строки формата и подстановка аргументов: <c>L.F("Продажа {0} проведена.", number)</c>.</summary>
    public static string F(string russianFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(russianFormat), args);

    /// <summary>Текст ошибки для пользователя: бизнес-ошибки ядра переводятся (вместе с подставленными значениями).</summary>
    public static string Message(Exception ex) => ex switch
    {
        Core.Common.BusinessException { Format: { } format } b => F(format, b.Args),
        _ => T(ex.Message)
    };

#if DEBUG
    /// <summary>Строки без перевода (отладка: App сохраняет их в %LocalAppData%\MiniTrade\missing-*.txt).</summary>
    public static readonly SortedSet<string> Missing = new(StringComparer.Ordinal);
#endif

    private static bool TryRead(string file, out LanguageInfo info, out Dictionary<string, string> strings)
    {
        info = Russian;
        strings = new();
        try
        {
            if (!File.Exists(file)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var code = Path.GetFileNameWithoutExtension(file);
            var name = code;
            var culture = code;
            if (root.TryGetProperty("language", out var lang))
            {
                if (lang.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } nn) name = nn;
                if (lang.TryGetProperty("culture", out var c) && c.GetString() is { Length: > 0 } cc) culture = cc;
            }
            if (root.TryGetProperty("strings", out var map) && map.ValueKind == JsonValueKind.Object)
                foreach (var p in map.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) strings[p.Name] = p.Value.GetString()!;
            info = new LanguageInfo(code, name, culture);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Перевод в XAML: <c>Text="{l:T 'Сохранить'}"</c>. Значение берётся при создании окна/экрана.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
