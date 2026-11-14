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

    /// <summary>Смена типа весов подставляет порт по умолчанию (TMA — 4001, CAS — 20304), если порт не менялся вручную.</summary>
    private void OnScaleTypeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string port, DataContext: MiniTrade.Core.Domain.ScaleDevice scale }) return;
        if (scale.Port is 0 or 4001 or 20304 && scale.Port.ToString() != port)
        {
            scale.Port = int.Parse(port);
            FindInTemplate(sender as DependencyObject)?.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateTarget();
        }
    }

    /// <summary>Rongta: пустой путь к файлу PLU заполняется значением по умолчанию.</summary>
    private void OnRongtaClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MiniTrade.Core.Domain.ScaleDevice scale } || !string.IsNullOrWhiteSpace(scale.FilePath)) return;
        scale.FilePath = MiniTrade.Core.Domain.ScaleDevice.RongtaDefaultPath;
        FindInTemplate(sender as DependencyObject, "ScaleFileBox")?.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateTarget();
    }

    private static System.Windows.Controls.TextBox? FindInTemplate(DependencyObject? d, string name = "ScalePortBox")
    {
        while (d != null && d is not System.Windows.Controls.ContentPresenter) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d is System.Windows.Controls.ContentPresenter cp
            ? cp.ContentTemplate?.FindName(name, cp) as System.Windows.Controls.TextBox : null;
    }

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

    /// <summary>Генерация штрихкода: значение пишется в поле рядом (товар не уведомляет об изменениях сам).</summary>
    private async void OnGenerateBarcode(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Parent: System.Windows.Controls.DockPanel panel }
            || DataContext is not CatalogEditViewModel<MiniTrade.Core.Domain.Product> vm) return;
        var box = panel.Children.OfType<System.Windows.Controls.TextBox>().FirstOrDefault();
        if (box is null) return;
        if (await vm.GenerateBarcodeAsync(replacing: !string.IsNullOrWhiteSpace(box.Text)) is { } code)
        {
            box.Text = code;
            box.Focus();
            box.SelectAll();
        }
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
