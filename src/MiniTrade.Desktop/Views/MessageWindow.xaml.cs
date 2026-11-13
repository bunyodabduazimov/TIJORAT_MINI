using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.Views;

public enum MessageKind { Info, Warning, Error, Question }

/// <summary>Окно сообщения/подтверждения в стиле приложения. true — «Да»/«OK».</summary>
public partial class MessageWindow : Window, IModalWindow
{
    private readonly bool _isQuestion;

    public MessageWindow(string message, string title, MessageKind kind)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        _isQuestion = kind == MessageKind.Question;

        (string glyph, string fg, string bg) = kind switch
        {
            MessageKind.Question => ("\U000F0625", "PrimaryBrush", "PrimaryLightBrush"),
            MessageKind.Warning => ("\U000F002A", "WarningBrush", "DangerLightBrush"),
            MessageKind.Error => ("\U000F05D6", "DangerBrush", "DangerLightBrush"),
            _ => ("\U000F02FD", "PrimaryBrush", "InfoLightBrush"),
        };
        IconGlyph.Text = glyph;
        IconGlyph.SetResourceReference(TextBlock.ForegroundProperty, fg);
        IconCircle.SetResourceReference(Border.BackgroundProperty, bg);

        if (_isQuestion)
        {
            YesButton.Content = L.T("Да");
            NoButton.Content = L.T("Нет");
            NoButton.Visibility = Visibility.Visible;
        }
        else
        {
            YesButton.Content = "OK";
        }

        Loaded += (_, _) => YesButton.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
            else if (e.Key == Key.Enter && !NoButton.IsKeyboardFocused) { DialogResult = true; e.Handled = true; }
        };
    }

    private void OnYes(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnNo(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    /// <summary>Показать сообщение модально поверх активного окна.</summary>
    public static bool Show(string message, string title, MessageKind kind) =>
        ModalHost.ShowDialog(new MessageWindow(message, title, kind)) == true;
}
