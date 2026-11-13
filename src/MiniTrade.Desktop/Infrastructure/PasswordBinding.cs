using System.Windows;
using System.Windows.Controls;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Двусторонняя привязка пароля: <c>&lt;PasswordBox inf:PasswordBinding.Value="{Binding X}"/&gt;</c>.</summary>
public static class PasswordBinding
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(string), typeof(PasswordBinding),
        // Значение по умолчанию — служебное, чтобы первое присвоение (даже null) вызвало подписку на PasswordChanged
        new FrameworkPropertyMetadata("\u0001",FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(PasswordBinding));

    public static string? GetValue(DependencyObject d) => (string?)d.GetValue(ValueProperty);
    public static void SetValue(DependencyObject d, string? value) => d.SetValue(ValueProperty, value);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        if (!(bool)box.GetValue(HookedProperty))
        {
            box.SetValue(HookedProperty, true);
            box.PasswordChanged += (_, _) => SetValue(box, box.Password);
        }
        var value = e.NewValue as string ?? "";
        if (box.Password != value) box.Password = value;
    }
}
