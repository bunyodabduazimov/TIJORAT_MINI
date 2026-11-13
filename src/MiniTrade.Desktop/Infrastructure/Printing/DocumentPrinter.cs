using System.IO;
using System.IO.Packaging;
using System.Printing;
using System.Windows.Documents;
using System.Windows.Xps.Packaging;
using MiniTrade.Desktop.Views;

namespace MiniTrade.Desktop.Infrastructure.Printing;

/// <summary>На какой принтер печатать: основной (ордера и прочее), чеки, накладные или этикетки.</summary>
public enum PrinterKind { Document, Receipt, Invoice, Label }

/// <summary>Печать документа: окно предпросмотра (если включено в настройках) или сразу на принтер.</summary>
public static class DocumentPrinter
{
    public static void Print(string title, IDocumentPaginatorSource document, int copies = 1, PrinterKind kind = PrinterKind.Document)
    {
        if (App.Ui.Print.ShowPreview)
            ModalHost.ShowDialog(new PrintPreviewWindow(title, document, Math.Max(1, copies), kind));
        else
            Send(title, document, copies, kind);
    }

    /// <summary>Отправка на принтер; false — пользователь отменил выбор принтера.</summary>
    public static bool Send(string title, IDocumentPaginatorSource document, int copies, PrinterKind kind)
    {
        if (PrintSetup.Prepare(kind) is not { } dialog) return false;
        var paginator = document.DocumentPaginator;
        var size = paginator.PageSize;
        try
        {
            var ticket = dialog.PrintTicket;
            ticket.PageOrientation = size.Width > size.Height ? PageOrientation.Landscape : PageOrientation.Portrait;
            // Чековая лента и этикетки — свой размер бумаги (A4 оставляем как у принтера)
            if (!IsA4(size))
                ticket.PageMediaSize = size.Width > size.Height ? new PageMediaSize(size.Height, size.Width) : new PageMediaSize(size.Width, size.Height);
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException) { }

        for (var i = 0; i < Math.Max(1, copies); i++)
            dialog.PrintDocument(paginator, title);
        return true;
    }

    private static bool IsA4(System.Windows.Size s)
    {
        var a4 = PrintDocuments.A4;
        return (Math.Abs(s.Width - a4.Width) < 3 && Math.Abs(s.Height - a4.Height) < 3)
            || (Math.Abs(s.Width - a4.Height) < 3 && Math.Abs(s.Height - a4.Width) < 3);
    }
}

/// <summary>
/// Документ для DocumentViewer: FixedDocument показывается как есть, FlowDocument
/// разбивается на страницы через XPS в памяти. Предыдущий документ освобождается при показе нового.
/// </summary>
public sealed class PreviewHolder : IDisposable
{
    private Uri? _uri;
    private XpsDocument? _xps;

    public IDocumentPaginatorSource? Show(IDocumentPaginatorSource? source)
    {
        Dispose();
        if (source is null or FixedDocument or FixedDocumentSequence) return source;

        var package = Package.Open(new MemoryStream(), FileMode.Create, FileAccess.ReadWrite);
        _uri = new Uri($"pack://preview{Guid.NewGuid():N}.xps");
        PackageStore.AddPackage(_uri, package);
        _xps = new XpsDocument(package, CompressionOption.NotCompressed, _uri.AbsoluteUri);
        XpsDocument.CreateXpsDocumentWriter(_xps).Write(source.DocumentPaginator);
        return _xps.GetFixedDocumentSequence();
    }

    public void Dispose()
    {
        _xps?.Close();
        _xps = null;
        if (_uri is not null) PackageStore.RemovePackage(_uri);
        _uri = null;
    }
}
