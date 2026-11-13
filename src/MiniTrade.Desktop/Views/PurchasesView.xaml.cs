using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Views;

public partial class PurchasesView : UserControl
{
    public PurchasesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is DocumentEditorBase old) old.LineAdded -= OnLineAdded;
            if (e.NewValue is DocumentEditorBase vm) vm.LineAdded += OnLineAdded;
        };
    }

    /// <summary>После добавления товара — фокус на его количество в таблице (можно сразу ввести).</summary>
    private void OnLineAdded(object? sender, DocumentLineViewModel line) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            LinesGrid.SelectedItem = line;
            LinesGrid.ScrollIntoView(line);
            LinesGrid.UpdateLayout();
            if (LinesGrid.ItemContainerGenerator.ContainerFromItem(line) is not DataGridRow row) return;
            if (FindChild<TextBox>(row) is not { } box) return; // первый TextBox строки — «Количество»
            Keyboard.Focus(box);
            box.SelectAll();
        });

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }
}
