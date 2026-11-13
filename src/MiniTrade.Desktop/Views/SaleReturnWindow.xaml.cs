using System.Windows;
using System.Windows.Input;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Views;

/// <summary>Модальное окно возврата по продаже (модель — <see cref="SaleReturnViewModel"/>).</summary>
public partial class SaleReturnWindow : Window, IModalWindow
{
    public SaleReturnWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ICloseRequest old) old.CloseRequested -= OnCloseRequested;
            if (e.NewValue is ICloseRequest vm)
            {
                vm.CloseRequested += OnCloseRequested;
                Width = vm.DialogWidth + ShadowRoom.Margin.Left + ShadowRoom.Margin.Right;
            }
        };
    }

    private void OnCloseRequested(object? sender, bool posted) => DialogResult = posted;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
