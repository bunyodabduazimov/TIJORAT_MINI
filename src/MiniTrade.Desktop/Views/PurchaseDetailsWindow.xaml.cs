using System.Windows;
using System.Windows.Input;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.Views;

/// <summary>Модальное окно просмотра закупки (модель — <see cref="ViewModels.PurchasesViewModel"/>).</summary>
public partial class PurchaseDetailsWindow : Window, IModalWindow
{
    public PurchaseDetailsWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
