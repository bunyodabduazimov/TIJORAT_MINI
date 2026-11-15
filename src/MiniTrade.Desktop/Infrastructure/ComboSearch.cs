using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>
/// Поиск в выпадающем списке ComboBox (как select2): в самом поле ввод запрещён,
/// при открытии списка сверху появляется поле поиска (PART_Search в шаблоне Theme.xaml),
/// строки, не содержащие текст поиска, скрываются. Включено для всех ComboBox стилем.
/// Фильтр не трогает общую CollectionView источника — другие списки с теми же данными не затрагиваются.
/// </summary>
public static class ComboSearch
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ComboSearch), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    /// <summary>Текущий текст поиска (читают строки списка через MatchConverter).</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(ComboSearch), new PropertyMetadata(""));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

    /// <summary>Ничего не найдено — шаблон показывает подсказку.</summary>
    public static readonly DependencyProperty NoMatchesProperty = DependencyProperty.RegisterAttached(
        "NoMatches", typeof(bool), typeof(ComboSearch), new PropertyMetadata(false));

    public static bool GetNoMatches(DependencyObject d) => (bool)d.GetValue(NoMatchesProperty);
    public static void SetNoMatches(DependencyObject d, bool value) => d.SetValue(NoMatchesProperty, value);

    /// <summary>[элемент, текст поиска, DisplayMemberPath] → Visible / Collapsed.</summary>
    public static readonly IMultiValueConverter MatchConverter = new MatchVisibilityConverter();

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox cb) return;
        cb.DropDownOpened -= OnOpened;
        cb.DropDownClosed -= OnClosed;
        cb.PreviewTextInput -= OnComboTextInput;
        cb.PreviewMouseLeftButtonDown -= OnItemClickGuard;
        cb.PreviewMouseLeftButtonUp -= OnItemClickGuard;
        if (e.NewValue is not true) return;
        cb.PreviewMouseLeftButtonDown += OnItemClickGuard;
        cb.PreviewMouseLeftButtonUp += OnItemClickGuard;
        cb.DropDownOpened += OnOpened;
        cb.DropDownClosed += OnClosed;
        cb.PreviewTextInput += OnComboTextInput;
    }

    private static TextBox? SearchBox(ComboBox cb)
    {
        cb.ApplyTemplate();
        if (cb.Template?.FindName("PART_Search", cb) is not TextBox box) return null;
        if (box.Tag is not ComboBox) // подписываемся один раз
        {
            box.Tag = cb;
            box.TextChanged += OnSearchChanged;
            box.PreviewKeyDown += OnSearchKeyDown;
            // Фокус в поле поиска шлёт RequestBringIntoView, который из Popup всплывает
            // до ScrollViewer страницы: страница прокручивается и список тут же закрывается.
            if (cb.Template.FindName("DropDown", cb) is FrameworkElement drop)
                drop.RequestBringIntoView += (_, e) => e.Handled = true;
        }
        return box;
    }

    private static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox cb || SearchBox(cb) is not { } box) return;
        box.Text = "";
        cb.SetValue(OpenedAtProperty, Environment.TickCount64);
        // После того как ComboBox сам переведёт фокус на выбранную строку
        cb.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
        });
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is not ComboBox cb) return;
        if (SearchBox(cb) is { } box) box.Text = "";
        if (cb.IsKeyboardFocusWithin) cb.Focus();
    }

    // Пока список раскрывается, щелчок по строке не выбирает её —
    // иначе нажатие у края поля сразу выбирает строку, которая «выехала» под курсор.
    private const long OpenGuardMs = 350;

    private static readonly DependencyProperty OpenedAtProperty = DependencyProperty.RegisterAttached(
        "OpenedAt", typeof(long), typeof(ComboSearch), new PropertyMetadata(0L));

    private static void OnItemClickGuard(object sender, MouseButtonEventArgs e)
    {
        var cb = (ComboBox)sender;
        if (!cb.IsDropDownOpen) return;
        if (Environment.TickCount64 - (long)cb.GetValue(OpenedAtProperty) > OpenGuardMs) return;
        if (e.OriginalSource is DependencyObject src && ItemsControl.ContainerFromElement(cb, src) is ComboBoxItem)
            e.Handled = true;
    }

    // Набор текста на закрытом списке (или на строке списка) — открываем и пишем в поиск
    private static void OnComboTextInput(object sender, TextCompositionEventArgs e)
    {
        var cb = (ComboBox)sender;
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (SearchBox(cb) is not { } box || ReferenceEquals(e.OriginalSource, box)) return;
        e.Handled = true;
        if (!cb.IsDropDownOpen) cb.IsDropDownOpen = true;
        var text = box.Text + e.Text;
        cb.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Text = text;
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
        });
    }

    private static void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.Tag is not ComboBox cb) return;
        var text = box.Text.Trim();
        SetText(cb, text);
        SetNoMatches(cb, text.Length > 0 && !cb.Items.Cast<object>().Any(i => Matches(i, cb.DisplayMemberPath, text)));
    }

    private static void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.Tag is not ComboBox cb) return;
        switch (e.Key)
        {
            case Key.Enter:
                var first = FirstMatchIndex(cb);
                if (first >= 0) cb.SelectedIndex = first;
                cb.IsDropDownOpen = false;
                e.Handled = true;
                break;
            case Key.Escape:
                cb.IsDropDownOpen = false;
                e.Handled = true;
                break;
            case Key.Down:
                // Переходим в список: дальше стрелки работают по видимым строкам
                var index = cb.SelectedIndex >= 0 && Matches(cb.SelectedItem, cb.DisplayMemberPath, GetText(cb))
                    ? cb.SelectedIndex : FirstMatchIndex(cb);
                if (index >= 0 && cb.ItemContainerGenerator.ContainerFromIndex(index) is ComboBoxItem item)
                {
                    item.BringIntoView();
                    item.Focus();
                }
                e.Handled = true;
                break;
            case Key.Up:
                e.Handled = true; // не даём ComboBox листать скрытые строки
                break;
        }
    }

    private static int FirstMatchIndex(ComboBox cb)
    {
        var text = GetText(cb);
        for (var i = 0; i < cb.Items.Count; i++)
            if (Matches(cb.Items[i], cb.DisplayMemberPath, text)) return i;
        return -1;
    }

    private static bool Matches(object? item, string? path, string? text) =>
        string.IsNullOrEmpty(text) || ItemText(item, path).Contains(text, StringComparison.CurrentCultureIgnoreCase);

    private static string ItemText(object? item, string? path)
    {
        if (item is null) return "";
        if (!string.IsNullOrEmpty(path))
        {
            var prop = item.GetType().GetProperty(path);
            if (prop is not null) return prop.GetValue(item)?.ToString() ?? "";
        }
        return item.ToString() ?? "";
    }

    private sealed class MatchVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            Matches(values.ElementAtOrDefault(0), values.ElementAtOrDefault(2) as string, values.ElementAtOrDefault(1) as string)
                ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
