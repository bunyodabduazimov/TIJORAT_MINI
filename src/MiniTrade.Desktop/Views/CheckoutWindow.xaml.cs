using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Views;

/// <summary>Модальное окно оформления продажи. Модель — SalesViewModel (чек остаётся тем же).</summary>
public partial class CheckoutWindow : Window, IModalWindow
{
    public CheckoutWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SalesViewModel old) { old.SalePosted -= OnPosted; old.PropertyChanged -= OnVmChanged; }
            if (e.NewValue is SalesViewModel vm) { vm.SalePosted += OnPosted; vm.PropertyChanged += OnVmChanged; }
            UpdateQuickAmounts();
        };
        Closed += (_, _) => { if (DataContext is SalesViewModel vm) { vm.SalePosted -= OnPosted; vm.PropertyChanged -= OnVmChanged; } };
        // Кассир сразу вводит сумму первой оплаты
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (FindTextBox(PaymentsList) is not { } box) return;
            Keyboard.Focus(box);
            box.SelectAll();
        });
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SalesViewModel.ToPay) or null or "") UpdateQuickAmounts();
    }

    /// <summary>Быстрые суммы — ближайшие «круглые» суммы выше суммы к оплате (173 → 180, 200, 500).</summary>
    private void UpdateQuickAmounts()
    {
        if (DataContext is not SalesViewModel vm) return;
        var values = SuggestAmounts(vm.ToPay);
        var buttons = QuickAmounts.Children.OfType<System.Windows.Controls.Button>().ToList();
        for (var i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            if (i < values.Count)
            {
                b.Visibility = Visibility.Visible;
                b.Content = values[i].ToString("#,0", System.Globalization.CultureInfo.CurrentCulture);
                b.Tag = "set:" + values[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else b.Visibility = Visibility.Hidden;
        }
    }

    internal static List<decimal> SuggestAmounts(decimal total)
    {
        var result = new List<decimal>();
        if (total <= 0) return result;
        foreach (var step in new decimal[] { 10, 50, 100, 500, 1000, 5000, 10000, 50000, 100000 })
        {
            var v = Math.Ceiling(total / step) * step;
            if (v <= total) v += step;
            if (!result.Contains(v)) result.Add(v);
            if (result.Count == 3) break;
        }
        return result;
    }

    private static System.Windows.Controls.TextBox? FindTextBox(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.TextBox tb) return tb;
            if (FindTextBox(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Последнее поле ввода, в котором был курсор (сумма, скидка или примечание) — в него вводит цифровая клавиатура.</summary>
    private System.Windows.Controls.TextBox? _target;

    protected override void OnPreviewGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnPreviewGotKeyboardFocus(e);
        if (e.NewFocus is System.Windows.Controls.TextBox tb) _target = tb;
    }

    /// <summary>Нажатие на цифровой клавиатуре: цифра, запятая, стереть, очистить, «вся сумма».</summary>
    private void OnKeypad(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Button { Tag: string key }) return;
        var tb = _target ?? FindTextBox(PaymentsList);
        if (tb is null) return;
        // Быстрые суммы и «Вся сумма» относятся к оплате: из примечания/скидки — в первую оплату
        if ((key == "all" || key.StartsWith("set:")) && tb.DataContext is not PaymentPart && FindTextBox(PaymentsList) is { } pay) tb = pay;
        var numeric = Ui.GetNumeric(tb);
        switch (key)
        {
            case "clear":
                tb.Clear();
                break;
            case "back":
                if (tb.SelectionLength > 0) tb.SelectedText = "";
                else if (tb.CaretIndex > 0)
                {
                    var at = tb.CaretIndex - 1;
                    tb.Text = tb.Text.Remove(at, 1);
                    tb.CaretIndex = at;
                }
                break;
            case var q when q.StartsWith("set:"):
                // Быстрая сумма — вписать в поле оплаты
                if (tb.DataContext is PaymentPart p && decimal.TryParse(q[4..], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    p.Amount = v;
                tb.CaretIndex = tb.Text.Length;
                break;
            case "all":
                // В поле оплаты — добавить оставшуюся сумму к оплате
                if (tb.DataContext is PaymentPart part && DataContext is SalesViewModel vm) part.Amount += vm.Remainder;
                tb.CaretIndex = tb.Text.Length;
                break;
            default:
                var text = key == "dec" ? System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator : key;
                // Одна запятая: если она уже есть вне выделения — не добавляем
                if (numeric && key == "dec" && tb.Text.Remove(tb.SelectionStart, tb.SelectionLength) is var rest && (rest.Contains(',') || rest.Contains('.'))) break;
                // «0» в поле заменяется первой цифрой, а не дописывается
                if (numeric && tb.Text == "0" && key != "dec") tb.Clear();
                var pos = tb.SelectionStart;
                tb.SelectedText = text;
                tb.SelectionLength = 0;
                tb.CaretIndex = pos + text.Length;
                break;
        }
        Keyboard.Focus(tb);
    }

    /// <summary>Esc — вернуться к чеку, F8 — отложить чек.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; }
        else if (e.Key == Key.F8) { e.Handled = true; OnSaveDraft(this, e); }
    }

    private void OnPosted(object? sender, EventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSaveDraft(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SalesViewModel vm) return;
        vm.SaveDraftCommand.Execute(null);
        DialogResult = false;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
