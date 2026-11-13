using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Views;

/// <summary>Модальное окно оформления продажи. Модель — SalesViewModel (чек остаётся тем же).</summary>
public partial class CheckoutWindow : Window, IModalWindow
{
    public CheckoutWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SalesViewModel old) old.SalePosted -= OnPosted;
            if (e.NewValue is SalesViewModel vm) vm.SalePosted += OnPosted;
        };
        Closed += (_, _) => { if (DataContext is SalesViewModel vm) vm.SalePosted -= OnPosted; };
        // Кассир сразу вводит полученную сумму
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Keyboard.Focus(ReceivedBox);
            ReceivedBox.SelectAll();
        });
    }

    private void OnPosted(object? sender, EventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSaveDraft(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SalesViewModel vm) return;
        vm.SaveDraftCommand.Execute(null);
        DialogResult = false;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
