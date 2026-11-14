using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>
/// Вкладки, скрытые правами роли (<c>Visibility="{l:Can …}"</c>), не должны оставаться выбранными:
/// иначе TabControl показывает содержимое скрытой вкладки. Выбирается первая видимая.
/// </summary>
public static class TabAccess
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(TabControl), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Fix((TabControl)s)));
        EventManager.RegisterClassHandler(typeof(TabControl), Selector.SelectionChangedEvent, new SelectionChangedEventHandler((s, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, s)) Fix((TabControl)s);
        }));
    }

    private static void Fix(TabControl tabs)
    {
        if (tabs.SelectedItem is TabItem { Visibility: Visibility.Visible }) return;
        var first = tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Visibility == Visibility.Visible);
        if (first is not null && !ReferenceEquals(first, tabs.SelectedItem))
            tabs.Dispatcher.BeginInvoke(() => tabs.SelectedItem = first);
    }
}
