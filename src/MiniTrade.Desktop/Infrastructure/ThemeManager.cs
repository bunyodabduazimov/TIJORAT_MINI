using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>
/// Светлая / тёмная / системная тема. Подменяет словарь палитры (Themes/Colors.*.xaml) в ресурсах приложения;
/// стили берут цвета через DynamicResource, поэтому всё перекрашивается сразу, без перезапуска.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri LightUri = new("pack://application:,,,/MiniTrade;component/Themes/Colors.Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/MiniTrade;component/Themes/Colors.Dark.xaml");
    private static bool _hooked;

    /// <summary>Выбор пользователя (System — следовать Windows).</summary>
    public static AppTheme Theme { get; private set; } = AppTheme.System;

    /// <summary>Фактически применённая тема.</summary>
    public static bool IsDark { get; private set; }

    public static event EventHandler? Changed;

    public static void Apply(AppTheme theme)
    {
        Theme = theme;
        EnsureHooks();
        var dark = theme == AppTheme.Dark || (theme == AppTheme.System && IsSystemDark());
        var app = Application.Current;
        if (app is null) return;

        // Палитра может быть подключена относительным путём (App.xaml: "Themes/Colors.Light.xaml")
        // или pack-URI — ищем по имени файла. Важно заменить, а не добавить: при двух палитрах
        // побеждает последняя в списке, и тема «не переключается».
        var dictionaries = app.Resources.MergedDictionaries;
        var palettes = dictionaries.Where(IsPalette).ToList();
        var wanted = dark ? DarkUri : LightUri;
        if (palettes.Count != 1 || !IsSame(palettes[0].Source, wanted))
        {
            var index = palettes.Count > 0 ? dictionaries.IndexOf(palettes[0]) : 0;
            foreach (var p in palettes) dictionaries.Remove(p);
            dictionaries.Insert(index, new ResourceDictionary { Source = wanted });
        }

        IsDark = dark;
        foreach (Window w in app.Windows) ApplyTitleBar(w);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static string FileName(Uri? uri) =>
        uri is null ? "" : System.IO.Path.GetFileName(uri.OriginalString).ToLowerInvariant();

    private static bool IsPalette(ResourceDictionary d) =>
        FileName(d.Source) is "colors.light.xaml" or "colors.dark.xaml";

    private static bool IsSame(Uri? a, Uri b) => FileName(a) == FileName(b);

    /// <summary>Тема Windows для приложений: HKCU\…\Personalize\AppsUseLightTheme (0 — тёмная).</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    private static void EnsureHooks()
    {
        if (_hooked) return;
        _hooked = true;

        // Системная тема: реагируем на переключение в параметрах Windows
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Theme == AppTheme.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
        };

        // Тёмный заголовок для всех окон со стандартной рамкой, включая открытые позже
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => ApplyTitleBar((Window)s)));
    }

    // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 (Windows 10 20H1+ / 11), 19 — более ранние сборки Windows 10
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void ApplyTitleBar(Window window)
    {
        if (window.WindowStyle == WindowStyle.None) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var value = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
    }
}
