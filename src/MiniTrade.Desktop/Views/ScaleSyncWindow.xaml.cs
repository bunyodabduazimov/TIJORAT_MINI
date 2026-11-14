using System.Windows;
using System.Windows.Input;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.Views;

/// <summary>Окно «Обновление весов» (модель — <see cref="ViewModels.ScaleSyncViewModel"/>).</summary>
public partial class ScaleSyncWindow : Window, IModalWindow
{
    public ScaleSyncWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
}
