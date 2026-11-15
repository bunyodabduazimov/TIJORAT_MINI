using System.Windows;
using System.Windows.Input;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Views;

/// <summary>Модальное окно добавления/изменения пользователя (модель — <see cref="UserEditViewModel"/>).</summary>
public partial class UserEditWindow : Window, IModalWindow
{
    public UserEditWindow()
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

    /// <summary>В поле времени — только цифры и двоеточие.</summary>
    private void OnTimeInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(c => !char.IsDigit(c) && c != ':');

    private void OnCloseRequested(object? sender, bool result) => DialogResult = result;

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
