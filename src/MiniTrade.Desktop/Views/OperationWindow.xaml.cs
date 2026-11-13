using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Views;

/// <summary>Модальное окно кассовой операции (приход, расход, перемещение, платёж контрагенту/от контрагента).</summary>
public partial class OperationWindow : Window, IModalWindow
{
    public OperationWindow()
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
        // Сумма — главное поле: фокус сразу в неё, текст выделен для ввода поверх «0»
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Keyboard.Focus(AmountBox);
            AmountBox.SelectAll();
        });
    }

    private void OnCloseRequested(object? sender, bool posted) => DialogResult = posted;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
