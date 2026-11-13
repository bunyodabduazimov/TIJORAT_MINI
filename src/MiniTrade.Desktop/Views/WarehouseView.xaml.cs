using System.Windows;
using System.Windows.Controls;

namespace MiniTrade.Desktop.Views;

public partial class WarehouseView : UserControl
{
    private DateTime _filterClosedAt;

    public WarehouseView() => InitializeComponent();

    // Повторный щелчок по кнопке при открытой панели — закрыть (а не открыть снова сразу после авто-закрытия)
    private void BalanceFilter_Click(object sender, RoutedEventArgs e)
    {
        if ((DateTime.Now - _filterClosedAt).TotalMilliseconds < 250) return;
        BalanceFilterPopup.IsOpen = true;
    }

    private void BalanceFilterPopup_Closed(object? sender, EventArgs e) => _filterClosedAt = DateTime.Now;

    private void MovementFilter_Click(object sender, RoutedEventArgs e)
    {
        if ((DateTime.Now - _filterClosedAt).TotalMilliseconds < 250) return;
        MovementFilterPopup.IsOpen = true;
    }

    private void MovementFilterPopup_Closed(object? sender, EventArgs e) => _filterClosedAt = DateTime.Now;

    private void TransferFilter_Click(object sender, RoutedEventArgs e)
    {
        if ((DateTime.Now - _filterClosedAt).TotalMilliseconds < 250) return;
        TransferFilterPopup.IsOpen = true;
    }

    private void TransferFilterPopup_Closed(object? sender, EventArgs e) => _filterClosedAt = DateTime.Now;

    private void InventoryFilter_Click(object sender, RoutedEventArgs e)
    {
        if ((DateTime.Now - _filterClosedAt).TotalMilliseconds < 250) return;
        InventoryFilterPopup.IsOpen = true;
    }

    private void InventoryFilterPopup_Closed(object? sender, EventArgs e) => _filterClosedAt = DateTime.Now;
}
