using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MiniTrade.Desktop.Infrastructure;
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
        // Фото в чеке — как столбец «Фото» витрины (флажок в «Параметрах кассы»)
        Resources["CartPhotoVisibility"] = ColPhoto.Visibility;
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(DataGridColumn.VisibilityProperty, typeof(DataGridColumn))
            .AddValueChanged(ColPhoto, (_, _) => Resources["CartPhotoVisibility"] = ColPhoto.Visibility);
        ApplyHiddenColumns();
        if (App.Ui.SalesCartWidth is double w && w >= CartColumn.MinWidth)
            CartColumn.Width = new GridLength(Math.Min(w, CartColumn.MaxWidth));
        Loaded += (_, _) =>
        {
            FocusSearch();
            // F8/F9 — с уровня окна: KeyBinding вида срабатывает, только когда фокус внутри него
            _host = Window.GetWindow(this);
            if (_host is not null) { _host.PreviewKeyDown -= OnHostKeyDown; _host.PreviewKeyDown += OnHostKeyDown; }
        };
        Unloaded += (_, _) => { if (_host is not null) _host.PreviewKeyDown -= OnHostKeyDown; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F2) return;
            FocusSearch();
            e.Handled = true;
        };
    }

    private Window? _host;

    /// <summary>F9 — к оплате, F8 — отложить чек; только пока касса на экране.</summary>
    private void OnHostKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible || Vm is not { } vm || (e.Key != Key.F8 && e.Key != Key.F9)) return;
        System.Windows.Input.ICommand cmd = e.Key == Key.F9 ? vm.CheckoutCommand : vm.SaveDraftCommand;
        if (cmd.CanExecute(null)) cmd.Execute(null);
        e.Handled = true;
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

    /// <summary>Столбцы таблицы витрины, которые можно скрыть (ключ — для ui.json).</summary>
    private IEnumerable<PosColumnOption> ColumnOptions() =>
    [
        new("Photo", L.T("Фото"), ColPhoto),
        new("Sku", L.T("Артикул"), ColSku),
        new("Barcode", L.T("Штрихкод"), ColBarcode),
        new("Stock", L.T("Остаток"), ColStock),
        new("Price", L.T("Цена"), ColPrice),
        new("Qty", L.T("Кол-во"), ColQty),
    ];

    /// <summary>Скрыть столбцы, отключённые в «Параметрах кассы».</summary>
    private void ApplyHiddenColumns()
    {
        foreach (var c in new (string Key, DataGridColumn Col)[] { ("Photo", ColPhoto), ("Sku", ColSku), ("Barcode", ColBarcode), ("Stock", ColStock), ("Price", ColPrice), ("Qty", ColQty) })
            c.Col.Visibility = App.Ui.PosHiddenColumns.Contains(c.Key) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Кнопка «Параметры» — модальное окно настроек кассы и столбцов таблицы.</summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        ModalHost.ShowDialog(new PosSettingsWindow(ColumnOptions()) { DataContext = DataContext });
        FocusSearch();
    }

    /// <summary>Добавленная или выбранная строка чека всегда видна — прокручиваем к ней.</summary>
    private void OnCartSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid grid && grid.SelectedItem is { } item)
            Dispatcher.BeginInvoke(() => grid.ScrollIntoView(item), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnCartKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Vm is not { SelectedLine: { } line } vm) return;
        // Del в поле количества/цены — обычное удаление символа
        if (Keyboard.FocusedElement is TextBox) return;
        vm.RemoveFromCartCommand.Execute(line);
        e.Handled = true;
    }
}
