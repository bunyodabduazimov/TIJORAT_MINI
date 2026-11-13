using System.Windows;
using System.Windows.Media;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ApplyScale();
        UiSettings.ScaleChanged += ApplyScale;
        Closed += (_, _) => UiSettings.ScaleChanged -= ApplyScale;
    }

    /// <summary>Масштаб всего содержимого окна (настройка «Интерфейс» → «Масштаб»).</summary>
    private void ApplyScale()
    {
        var s = Math.Clamp(App.Ui.Scale, 0.8, 1.5);
        Root.LayoutTransform = Math.Abs(s - 1) < 0.001 ? Transform.Identity : new ScaleTransform(s, s);
    }
}
