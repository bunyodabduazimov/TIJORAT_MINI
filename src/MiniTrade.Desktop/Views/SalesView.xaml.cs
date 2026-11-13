using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Views;

/// <summary>
/// Продажи. Клавиатура быстрой кассы: F2 — строка поиска, ↑/↓ — выбор товара на витрине, Enter — в чек, Esc — очистить поиск,
/// Del в чеке — удалить строку, F9 — провести (KeyBinding в XAML). Клик по плитке/строке — товар в чек.
/// </summary>
public partial class SalesView : UserControl
{
    public SalesView()
    {
        InitializeComponent();
        if (App.Ui.SalesCartWidth is double w && w >= CartColumn.MinWidth)
            CartColumn.Width = new GridLength(Math.Min(w, CartColumn.MaxWidth));
        Loaded += (_, _) => FocusSearch();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F2) return;
            FocusSearch();
            e.Handled = true;
        };
    }

    private SalesViewModel? Vm => DataContext as SalesViewModel;

    /// <summary>Запоминаем ширину колонки чека после перетаскивания разделителя.</summary>
    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        App.Ui.SalesCartWidth = Math.Round(CartColumn.ActualWidth);
        App.Ui.Save();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SalesViewModel old) old.FocusSearchRequested -= OnFocusSearchRequested;
        if (e.NewValue is SalesViewModel vm) vm.FocusSearchRequested += OnFocusSearchRequested;
    }

    private void OnFocusSearchRequested(object? sender, EventArgs e) => FocusSearch();

    private void FocusSearch() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        if (!SearchBox.IsVisible) return;
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
    });

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        switch (e.Key)
        {
            case Key.Down: vm.MoveSelection(+1); ScrollToSelected(vm); e.Handled = true; break;
            case Key.Up: vm.MoveSelection(-1); ScrollToSelected(vm); e.Handled = true; break;
            case Key.Enter: vm.SearchEnterCommand.Execute(null); e.Handled = true; break;
            case Key.Escape: vm.ClearSearchCommand.Execute(null); e.Handled = true; break;
        }
    }

    private void ScrollToSelected(SalesViewModel vm)
    {
        if (vm.SelectedItem is null) return;
        if (vm.ShowTable) ShowcaseGrid.ScrollIntoView(vm.SelectedItem);
        else TileList.ScrollIntoView(vm.SelectedItem);
    }

    /// <summary>Клик по плитке или строке таблицы — товар сразу в чек (клик по заголовку/прокрутке игнорируется).</summary>
    private void OnShowcaseClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm) return;
        for (var el = e.OriginalSource as DependencyObject; el is not null && el != sender; el = System.Windows.Media.VisualTreeHelper.GetParent(el))
        {
            if (el is System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.DataGridColumnHeader) return;
            if (el is ListBoxItem { DataContext: PosItem } || el is DataGridRow { DataContext: PosItem }) 
            {
                vm.PickCommand.Execute(((FrameworkElement)el).DataContext);
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>
    /// Клик по категории. Элементы списка не фокусируемые (PlainItem), а без фокуса ListBox не выделяет элемент,
    /// поэтому категорию выбираем сами.
    /// </summary>
    /// <summary>Категории в одну строку: колесо мыши прокручивает их по горизонтали.</summary>
    private void OnCategoriesWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindScrollViewer((DependencyObject)sender) is not { } sv) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is System.Windows.Controls.ScrollViewer sv) return sv;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    private void OnCategoryClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm) return;
        for (var el = e.OriginalSource as DependencyObject; el is not null && el != sender; el = System.Windows.Media.VisualTreeHelper.GetParent(el))
        {
            if (el is System.Windows.Controls.Primitives.ScrollBar) return;
            if (el is ListBoxItem { DataContext: PosCategory c })
            {
                vm.SelectedPosCategory = c;
                e.Handled = true;
                return;
            }
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = true;

    private void OnCartKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Vm is not { SelectedLine: { } line } vm) return;
        // Del в поле количества/цены — обычное удаление символа
        if (Keyboard.FocusedElement is TextBox) return;
        vm.RemoveFromCartCommand.Execute(line);
        e.Handled = true;
    }
}
