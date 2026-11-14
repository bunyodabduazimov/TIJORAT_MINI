using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>
/// Вложенные свойства для полей ввода из темы (Themes/Theme.xaml):
/// <code>&lt;TextBox inf:Ui.Placeholder="Поиск…" inf:Ui.Icon="&amp;#xF0349;"/&gt;</code>
/// </summary>
public static class Ui
{
    /// <summary>Подсказка внутри пустого поля.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static string? GetPlaceholder(DependencyObject d) => (string?)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string? value) => d.SetValue(PlaceholderProperty, value);

    /// <summary>Глиф Material Design Icons слева в поле (например, &#xF0349; — поиск).</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static string? GetIcon(DependencyObject d) => (string?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, string? value) => d.SetValue(IconProperty, value);

    /// <summary>Кнопка «×» в шаблоне поля: очищает поле-владельца шаблона и возвращает в него фокус.</summary>
    public static readonly DependencyProperty ClearsTextProperty = DependencyProperty.RegisterAttached(
        "ClearsText", typeof(bool), typeof(Ui), new PropertyMetadata(false, (d, e) =>
        {
            if (d is ButtonBase b && e.NewValue is true)
                b.Click += (_, _) =>
                {
                    if (b.TemplatedParent is TextBox tb) { tb.Clear(); tb.Focus(); }
                };
        }));

    public static bool GetClearsText(DependencyObject d) => (bool)d.GetValue(ClearsTextProperty);
    public static void SetClearsText(DependencyObject d, bool value) => d.SetValue(ClearsTextProperty, value);

    /// <summary>
    /// Кнопка «×» в выпадающем списке: сбрасывает выбор. Показывается во всех списках, когда что-то выбрано;
    /// отключается явно: Ui.Clearable="False".
    /// </summary>
    public static readonly DependencyProperty ClearableProperty = DependencyProperty.RegisterAttached(
        "Clearable", typeof(bool), typeof(Ui), new PropertyMetadata(true));

    public static bool GetClearable(DependencyObject d) => (bool)d.GetValue(ClearableProperty);
    public static void SetClearable(DependencyObject d, bool value) => d.SetValue(ClearableProperty, value);

    /// <summary>Кнопка в шаблоне ComboBox: очищает выбор списка-владельца.</summary>
    public static readonly DependencyProperty ClearsSelectionProperty = DependencyProperty.RegisterAttached(
        "ClearsSelection", typeof(bool), typeof(Ui), new PropertyMetadata(false, (d, e) =>
        {
            if (d is ButtonBase b && e.NewValue is true)
                b.Click += (_, _) =>
                {
                    if (b.TemplatedParent is not ComboBox cb) return;
                    cb.SelectedIndex = -1;
                    cb.SelectedItem = null;
                    cb.Focus();
                };
        }));

    public static bool GetClearsSelection(DependencyObject d) => (bool)d.GetValue(ClearsSelectionProperty);
    public static void SetClearsSelection(DependencyObject d, bool value) => d.SetValue(ClearsSelectionProperty, value);

    /// <summary>[SelectedItem, Placeholder, Clearable, IsEnabled] → видимость кнопки «×» в списке.</summary>
    public static readonly IMultiValueConverter ComboClearVisibility = new ComboClearConverter();

    private sealed class ComboClearConverter : IMultiValueConverter
    {
        public object Convert(object[] v, Type t, object? p, System.Globalization.CultureInfo c)
        {
            var selected = v.Length > 0 && v[0] is not null && v[0] != DependencyProperty.UnsetValue;
            // Сбросить выбор можно в любом списке, кроме явно запрещённых (Ui.Clearable="False")
            var allowed = v.Length <= 2 || v[2] is not false;
            var enabled = v.Length <= 3 || v[3] is true;
            return selected && allowed && enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object v, Type[] t, object? p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>
    /// Только число в поле: цифры и один десятичный разделитель («,» или «.»).
    /// Буквы, пробелы и второй разделитель не вводятся; вставка из буфера проверяется так же.
    /// </summary>
    public static readonly DependencyProperty NumericProperty = DependencyProperty.RegisterAttached(
        "Numeric", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnNumericChanged));

    public static bool GetNumeric(DependencyObject d) => (bool)d.GetValue(NumericProperty);
    public static void SetNumeric(DependencyObject d, bool value) => d.SetValue(NumericProperty, value);

    private static readonly System.Text.RegularExpressions.Regex NumberPattern = new(@"^\d*([.,]\d*)?$");

    private static void OnNumericChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.PreviewTextInput -= OnNumericTextInput;
        box.PreviewKeyDown -= OnNumericKeyDown;
        DataObject.RemovePastingHandler(box, OnNumericPaste);
        if (e.NewValue is not true) return;
        box.PreviewTextInput += OnNumericTextInput;
        box.PreviewKeyDown += OnNumericKeyDown;
        DataObject.AddPastingHandler(box, OnNumericPaste);
    }

    /// <summary>
    /// Фокус на числовом поле (Ui.Numeric или привязка через DecimalConverter): «0» убирается,
    /// иначе число выделяется целиком — новый ввод заменяет его.
    /// </summary>
    public static void RegisterNumericFocus()
    {
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnNumericFocus));
        // Щелчок мышью по ещё не активному полю: иначе клик сразу снимет выделение
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnNumericMouseDown), true);
    }

    private static bool IsNumberBox(TextBox box) =>
        !box.IsReadOnly && (GetNumeric(box) ||
            System.Windows.Data.BindingOperations.GetBinding(box, TextBox.TextProperty)?.Converter is DecimalConverter);

    private static void OnNumericFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox box || !ReferenceEquals(e.NewFocus, box) || !IsNumberBox(box)) return;
        var s = (box.Text ?? "").Trim().Replace(" ", "").Replace('.', ',');
        if (s.Length > 0 && decimal.TryParse(s, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), out var d) && d == 0)
            box.Clear();
        else
            box.SelectAll();
    }

    private static void OnNumericMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || box.IsKeyboardFocusWithin || !IsNumberBox(box)) return;
        e.Handled = true;
        box.Focus();
    }

    /// <summary>Текст поля, каким он станет после ввода/вставки на месте выделения.</summary>
    private static string Proposed(TextBox box, string input)
    {
        // После обновления текста из привязки (напр. «45.» → «45») позиция курсора может устареть — ограничиваем
        var text = box.Text ?? "";
        var start = Math.Clamp(box.SelectionStart, 0, text.Length);
        var length = Math.Clamp(box.SelectionLength, 0, text.Length - start);
        return text.Remove(start, length).Insert(start, input);
    }

    private static void OnNumericTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!NumberPattern.IsMatch(Proposed((TextBox)sender, e.Text))) e.Handled = true;
    }

    // Пробел не проходит через TextInput — блокируем отдельно
    private static void OnNumericKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) e.Handled = true;
    }

    private static void OnNumericPaste(object sender, DataObjectPastingEventArgs e)
    {
        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
        // Пробелы-разделители тысяч («1 250,50») убираем, остальное должно быть числом
        var clean = text?.Replace(" ", "").Replace(" ", "").Trim();
        if (clean is null || !NumberPattern.IsMatch(Proposed((TextBox)sender, clean)))
        {
            e.CancelCommand();
            return;
        }
        if (clean != text)
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, clean);
            e.DataObject = data;
        }
    }

    /// <summary>
    /// Ввод даты с клавиатуры: только цифры и разделители «.», «/», «-» (не больше 10 символов).
    /// Включено для всех DatePicker стилем DatePickerTextBox.
    /// </summary>
    public static readonly DependencyProperty DateInputProperty = DependencyProperty.RegisterAttached(
        "DateInput", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnDateInputChanged));

    public static bool GetDateInput(DependencyObject d) => (bool)d.GetValue(DateInputProperty);
    public static void SetDateInput(DependencyObject d, bool value) => d.SetValue(DateInputProperty, value);

    private static readonly System.Text.RegularExpressions.Regex DatePattern = new(@"^[0-9./\-]{0,10}$");

    private static void OnDateInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.PreviewTextInput -= OnDateTextInput;
        box.PreviewKeyDown -= OnNumericKeyDown;
        DataObject.RemovePastingHandler(box, OnDatePaste);
        if (e.NewValue is not true) return;
        box.PreviewTextInput += OnDateTextInput;
        box.PreviewKeyDown += OnNumericKeyDown; // пробел тоже запрещён
        DataObject.AddPastingHandler(box, OnDatePaste);
    }

    private static void OnDateTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!DatePattern.IsMatch(Proposed((TextBox)sender, e.Text))) e.Handled = true;
    }

    private static void OnDatePaste(object sender, DataObjectPastingEventArgs e)
    {
        var text = (e.DataObject.GetData(DataFormats.UnicodeText) as string)?.Trim();
        if (text is null || !DatePattern.IsMatch(Proposed((TextBox)sender, text))) e.CancelCommand();
    }

    /// <summary>
    /// Нумерация строк DataGrid в заголовках строк (колонка «№»). Включена для всех таблиц стилем DataGrid.
    /// Номера пересчитываются при удалении/добавлении строк и сортировке.
    /// </summary>
    public static readonly DependencyProperty RowNumbersProperty = DependencyProperty.RegisterAttached(
        "RowNumbers", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnRowNumbersChanged));

    public static bool GetRowNumbers(DependencyObject d) => (bool)d.GetValue(RowNumbersProperty);
    public static void SetRowNumbers(DependencyObject d, bool value) => d.SetValue(RowNumbersProperty, value);

    /// <summary>С какого номера продолжать (для постраничных таблиц: 50 → вторая страница начинается с 51).</summary>
    public static readonly DependencyProperty RowNumberOffsetProperty = DependencyProperty.RegisterAttached(
        "RowNumberOffset", typeof(int), typeof(Ui), new PropertyMetadata(0, (d, _) => Renumber(d as DataGrid)));

    public static int GetRowNumberOffset(DependencyObject d) => (int)d.GetValue(RowNumberOffsetProperty);
    public static void SetRowNumberOffset(DependencyObject d, int value) => d.SetValue(RowNumberOffsetProperty, value);

    private static void OnRowNumbersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid || e.NewValue is not true) return;
        grid.LoadingRow += (_, args) => args.Row.Header = Number(grid, args.Row.GetIndex());
        ((System.Collections.Specialized.INotifyCollectionChanged)grid.Items).CollectionChanged += (_, _) => Renumber(grid);
    }

    private static string Number(DataGrid grid, int index) => (GetRowNumberOffset(grid) + index + 1).ToString();

    private static void Renumber(DataGrid? grid)
    {
        if (grid is null || !GetRowNumbers(grid)) return;
        // После того как таблица перестроит строки
        grid.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            for (var i = 0; i < grid.Items.Count; i++)
                if (grid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow row)
                    row.Header = Number(grid, i);
        });
    }

    /// <summary>Команда по двойному клику на строке DataGrid; параметр — запись строки.</summary>
    public static readonly DependencyProperty RowDoubleClickCommandProperty = DependencyProperty.RegisterAttached(
        "RowDoubleClickCommand", typeof(ICommand), typeof(Ui), new PropertyMetadata(null, OnRowDoubleClickChanged));

    public static ICommand? GetRowDoubleClickCommand(DependencyObject d) => (ICommand?)d.GetValue(RowDoubleClickCommandProperty);
    public static void SetRowDoubleClickCommand(DependencyObject d, ICommand? value) => d.SetValue(RowDoubleClickCommandProperty, value);

    private static void OnRowDoubleClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        grid.MouseDoubleClick -= OnGridDoubleClick;
        if (e.NewValue is not null) grid.MouseDoubleClick += OnGridDoubleClick;
    }

    private static void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Только по строкам: не по заголовкам, полосам прокрутки и кнопкам в ячейках
        for (var el = e.OriginalSource as DependencyObject; el is not null && el != sender; el = VisualTreeHelper.GetParent(el))
        {
            if (el is ButtonBase) return;
            if (el is DataGridRow row)
            {
                var cmd = GetRowDoubleClickCommand((DataGrid)sender);
                if (cmd?.CanExecute(row.Item) == true) cmd.Execute(row.Item);
                e.Handled = true;
                return;
            }
        }
    }

    // ── Кнопка-дропдаун: по клику открывает свой ContextMenu под кнопкой ──
    public static readonly DependencyProperty DropDownMenuProperty = DependencyProperty.RegisterAttached(
        "DropDownMenu", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnDropDownMenuChanged));

    public static bool GetDropDownMenu(DependencyObject d) => (bool)d.GetValue(DropDownMenuProperty);
    public static void SetDropDownMenu(DependencyObject d, bool value) => d.SetValue(DropDownMenuProperty, value);

    private static void OnDropDownMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase b) return;
        b.Click -= OnDropDownClick;
        if (e.NewValue is true) b.Click += OnDropDownClick;
    }

    private static void OnDropDownClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } b) return;
        menu.PlacementTarget = b;
        // Кнопка в правой половине окна («Создать») — меню выравниваем по её правому краю, в левой (действия строки) — по левому.
        // 10 px — поле под тень в стиле DropDownMenu.
        var window = Window.GetWindow(b);
        var alignRight = window is not null && b.TranslatePoint(new Point(0, 0), window).X > window.ActualWidth / 2;
        var shadow = menu.Style == (b.TryFindResource("DropDownMenu") as Style) ? 10 : 0;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(alignRight ? target.Width - popup.Width + shadow : -shadow, target.Height + (shadow > 0 ? 0 : 4)),
                PopupPrimaryAxis.Horizontal)];
        menu.MinWidth = b.ActualWidth;
        menu.DataContext = b.DataContext;
        menu.IsOpen = true;
    }

    // ── Своя рамка окна (стиль ChromeWindow): команды кнопок заголовка ──
    public static readonly DependencyProperty WindowCommandsProperty = DependencyProperty.RegisterAttached(
        "WindowCommands", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnWindowCommandsChanged));

    public static bool GetWindowCommands(DependencyObject d) => (bool)d.GetValue(WindowCommandsProperty);
    public static void SetWindowCommands(DependencyObject d, bool value) => d.SetValue(WindowCommandsProperty, value);

    private static void OnWindowCommandsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window w || e.NewValue is not true) return;
        w.CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(w)));
        w.CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(w)));
        w.CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(w)));
        w.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(w)));
    }
}
