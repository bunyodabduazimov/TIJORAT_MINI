using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.Infrastructure.Printing;

namespace MiniTrade.Desktop.Views;

/// <summary>Предпросмотр документа перед печатью.</summary>
public partial class PrintPreviewWindow : Window, IModalWindow
{
    private readonly string _title;
    private readonly IDocumentPaginatorSource _document;
    private readonly PrinterKind _kind;
    private readonly PreviewHolder _preview = new();

    public PrintPreviewWindow(string title, IDocumentPaginatorSource document, int copies, PrinterKind kind)
    {
        InitializeComponent();
        _title = title;
        _document = document;
        _kind = kind;

        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, area.Height - 20);
        Width = Math.Min(Width, area.Width - 20);

        Title = TitleText.Text = title;
        PrinterText.Text = L.F("Принтер: {0}", PrintSetup.PrinterFor(kind) is { Length: > 0 } p ? p : L.T("Принтер Windows по умолчанию"));
        CopiesBox.ItemsSource = Enumerable.Range(1, 10).ToList();
        CopiesBox.Text = copies.ToString();

        Viewer.Document = _preview.Show(document);
        // Кнопка печати на панели просмотра — тоже через настройки принтера программы
        Viewer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Print, (_, _) => OnPrint(this, new RoutedEventArgs())));
        // A4 — по ширине окна; чековая лента и этикетки — крупнее, чтобы было читаемо
        var pageWidth = document.DocumentPaginator.PageSize.Width;
        Loaded += (_, _) =>
        {
            if (pageWidth > 500) Viewer.FitToWidth();
            else Viewer.Zoom = Math.Clamp(420 / pageWidth * 100, 100, 300);
        };
        Closed += (_, _) => _preview.Dispose();
    }

    private void OnPrint(object sender, RoutedEventArgs e)
    {
        var copies = int.TryParse(CopiesBox.Text, out var c) ? Math.Clamp(c, 1, 999) : 1;
        if (DocumentPrinter.Send(_title, _document, copies, _kind)) DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
