using MiniTrade.Desktop.Infrastructure;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Views;

/// <summary>Модальное окно добавления/изменения записи справочника (открывается через ICatalogDialogs).</summary>
public partial class CatalogEditWindow : Window, IModalWindow
{
    public CatalogEditWindow()
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
        // Фокус — в первое поле формы
        // Фокус — в первое текстовое поле формы (у товара перед ним стоит кнопка фото)
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (FirstTextBox(Form) is { } box) Keyboard.Focus(box);
            else Form.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
    }

    private static System.Windows.Controls.TextBox? FirstTextBox(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.TextBox { IsEnabled: true, IsReadOnly: false, IsVisible: true } tb) return tb;
            if (FirstTextBox(child) is { } found) return found;
        }
        return null;
    }

    private void OnCloseRequested(object? sender, bool saved) => DialogResult = saved;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    // Перетаскивание файла изображения на место фото товара
    private static string? DroppedImage(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && PhotoProcessor.IsImageFile(files[0]) ? files[0] : null;

    private void OnPhotoDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedImage(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnPhotoDrop(object sender, DragEventArgs e)
    {
        if (DroppedImage(e) is { } file && DataContext is CatalogEditViewModel<MiniTrade.Core.Domain.Product> vm)
            vm.SetPhotoFromFile(file);
        e.Handled = true;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
