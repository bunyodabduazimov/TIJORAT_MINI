using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Views;

/// <summary>Содержимое окна активации (пересоздаётся при смене языка).</summary>
public partial class ActivationView : UserControl
{
    private bool _formatting;

    public ActivationView() => InitializeComponent();

    private void OnClose(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) Window.GetWindow(this)?.DragMove();
    }

    /// <summary>В поле телефона вводятся только цифры (вставка обрабатывается в OnPhoneChanged).</summary>
    private void OnPhoneTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsAsciiDigit);

    /// <summary>Номер приводится к маске выбранной страны; курсор остаётся после той же цифры.</summary>
    private void OnPhoneChanged(object sender, TextChangedEventArgs e)
    {
        if (_formatting || sender is not TextBox box || DataContext is not ActivationViewModel vm) return;
        var country = vm.SelectedCountry;
        var text = box.Text;
        var digitsBeforeCaret = text[..Math.Min(box.CaretIndex, text.Length)].Count(char.IsAsciiDigit);
        var digits = country.ExtractDigits(text);
        var formatted = country.Format(digits);
        if (formatted == text) return;

        _formatting = true;
        try { box.Text = formatted; }
        finally { _formatting = false; }
        box.CaretIndex = CaretAfterDigits(formatted, Math.Min(digitsBeforeCaret, digits.Length));
    }

    private static int CaretAfterDigits(string text, int digits)
    {
        if (digits <= 0) return 0;
        for (int i = 0, seen = 0; i < text.Length; i++)
            if (char.IsAsciiDigit(text[i]) && ++seen == digits) return i + 1;
        return text.Length;
    }
}
