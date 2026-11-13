using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Views;

public partial class AuditLogView : UserControl
{
    public AuditLogView() => InitializeComponent();

    // Щелчок по строке (не по заголовку/полосе прокрутки) — подробности записи
    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null && d != sender; d = VisualTreeHelper.GetParent(d))
        {
            if (d is DataGridRow { Item: AuditRow row })
            {
                ShowDetails(row);
                return;
            }
            if (d is not Visual) return;
        }
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ((DataGrid)sender).SelectedItem is AuditRow row)
        {
            ShowDetails(row);
            e.Handled = true;
        }
    }

    private static void ShowDetails(AuditRow row) => ModalHost.ShowDialog(new AuditDetailsWindow(row));
}
