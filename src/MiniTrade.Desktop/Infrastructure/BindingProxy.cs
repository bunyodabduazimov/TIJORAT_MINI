using System.Windows;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>
/// Передаёт DataContext туда, где его нет в визуальном дереве (например, в колонки DataGrid):
/// <c>&lt;inf:BindingProxy x:Key="Proxy" Data="{Binding}"/&gt;</c> и затем <c>{Binding Data.Prop, Source={StaticResource Proxy}}</c>.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
