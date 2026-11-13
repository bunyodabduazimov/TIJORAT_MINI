using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MiniTrade.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // Образец печати: A4 — по ширине панели, чековая лента и этикетка — крупнее
        DependencyPropertyDescriptor.FromProperty(DocumentViewer.DocumentProperty, typeof(DocumentViewer))
            .AddValueChanged(PreviewViewer, (_, _) => Dispatcher.BeginInvoke(FitPreview, DispatcherPriority.Loaded));
        DependencyPropertyDescriptor.FromProperty(DocumentViewer.ZoomProperty, typeof(DocumentViewer))
            .AddValueChanged(PreviewViewer, (_, _) => ZoomText.Text = $"{PreviewViewer.Zoom:0}%");
        PreviewViewer.SizeChanged += (_, e) => { if (e.WidthChanged) FitPreview(); };
        PreviewViewer.Loaded += (_, _) => HideViewerChrome();
    }

    /// <summary>Убираем стандартную светлую панель инструментов и строку поиска DocumentViewer — у нас своя панель масштаба.</summary>
    private void HideViewerChrome()
    {
        PreviewViewer.ApplyTemplate();
        if (PreviewViewer.Template?.FindName("PART_FindToolBarHost", PreviewViewer) is FrameworkElement find)
            find.Visibility = Visibility.Collapsed;
        foreach (var bar in Descendants<ToolBar>(PreviewViewer)) bar.Visibility = Visibility.Collapsed;
        ZoomText.Text = $"{PreviewViewer.Zoom:0}%";
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private void FitPreview()
    {
        if (PreviewViewer.Document is not { } doc || PreviewViewer.ActualWidth <= 0) return;
        var pageWidth = doc.DocumentPaginator.PageSize.Width;
        if (pageWidth > 500) PreviewViewer.FitToWidth();
        else PreviewViewer.Zoom = Math.Clamp((PreviewViewer.ActualWidth - 80) / pageWidth * 100 * 0.6, 100, 300);
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => PreviewViewer.IncreaseZoom();
    private void OnZoomOut(object sender, RoutedEventArgs e) => PreviewViewer.DecreaseZoom();
    private void OnZoomFit(object sender, RoutedEventArgs e) => FitPreview();
}
