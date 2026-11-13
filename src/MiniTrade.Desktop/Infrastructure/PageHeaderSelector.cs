using System.Windows;
using System.Windows.Controls;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>
/// Кнопки справа от заголовка страницы. Страница задаёт их ресурсом-шаблоном с ключом
/// «{ИмяМодели}.Header» (например, FinanceViewModel.Header в App.xaml); нет ресурса — нет кнопок.
/// </summary>
public sealed class PageHeaderSelector : DataTemplateSelector
{
    // Пустой шаблон обязателен: при null ContentControl берёт шаблон страницы по типу и рисует её второй раз
    private static readonly DataTemplate Empty = new();

    public override DataTemplate SelectTemplate(object? item, DependencyObject container) =>
        item is not null && (container as FrameworkElement)?.TryFindResource(item.GetType().Name + ".Header") is DataTemplate t ? t : Empty;
}
