using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Кнопка темы в заголовке окна: светлая ↔ тёмная, выбор сохраняется в ui.json.</summary>
public static class ThemeToggle
{
    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    static ThemeToggle() => ThemeManager.Changed += (_, _) => Notify();

    /// <summary>В тёмной теме — солнце (перейти на светлую), в светлой — луна.</summary>
    public static string Glyph => ThemeManager.IsDark ? "\U000F05A8" : "\U000F0594";

    public static string Tip => ThemeManager.IsDark ? L.T("Светлая тема") : L.T("Тёмная тема");

    public static ICommand Command { get; } = new RelayCommand(() =>
    {
        var theme = ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark;
        App.Ui.Theme = theme;
        App.Ui.Save();
        ThemeManager.Apply(theme);
        Notify();
    });

    private static void Notify()
    {
        StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(Glyph)));
        StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(Tip)));
    }
}
