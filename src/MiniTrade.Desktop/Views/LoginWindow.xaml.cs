using MiniTrade.Desktop.Infrastructure;
using System.Windows;
using System.Windows.Input;
using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not LoginViewModel vm) return;
            vm.LoggedIn += (_, _) => DialogResult = true;
            if (vm.SavedPassword is { } saved) Pwd.Password = saved;
        };
    }

    /// <summary>Текущий пароль — из скрытого или из открытого поля («глаз»).</summary>
    private string Password => ShowPwd.IsChecked == true ? PwdVisible.Text : Pwd.Password;

    // PasswordBox не поддерживает привязку — передаём пароль в команду напрямую
    private void OnSignIn(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm && vm.SignInCommand.CanExecute(Password))
            vm.SignInCommand.Execute(Password);
    }

    private void OnShowPassword(object sender, RoutedEventArgs e)
    {
        var show = ShowPwd.IsChecked == true;
        if (show)
        {
            PwdVisible.Text = Pwd.Password;
            PwdVisible.Visibility = Visibility.Visible;
            Pwd.Visibility = Visibility.Collapsed;
            PwdVisible.Focus();
            PwdVisible.CaretIndex = PwdVisible.Text.Length;
        }
        else
        {
            Pwd.Password = PwdVisible.Text;
            Pwd.Visibility = Visibility.Visible;
            PwdVisible.Visibility = Visibility.Collapsed;
            Pwd.Focus();
        }
        ShowPwd.ToolTip = show ? L.T("Скрыть пароль") : L.T("Показать пароль");
    }

    private void OnPasswordKey(object sender, KeyEventArgs e) => UpdateCapsWarning();

    private void OnPasswordFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateCapsWarning();

    private void UpdateCapsWarning() =>
        CapsWarning.Visibility = Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
