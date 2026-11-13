using System.Printing;
using System.Windows.Controls;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Выбор принтера по настройкам: принтер по умолчанию и печать без диалога.</summary>
public static class PrintSetup
{
    /// <summary>Имена установленных принтеров (локальные и сетевые подключения).</summary>
    public static IReadOnlyList<string> Printers()
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
                .Select(q => q.FullName).OrderBy(n => n).ToList();
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Готовый PrintDialog или null, если пользователь отменил печать.</summary>
    public static PrintDialog? Prepare(Printing.PrinterKind kind = Printing.PrinterKind.Document)
    {
        var dialog = new PrintDialog();
        if (FindQueue(PrinterFor(kind)) is { } queue) dialog.PrintQueue = queue;
        if (App.Ui.PrintWithoutDialog)
        {
            try
            {
                _ = dialog.PrintQueue; // нет ни одного принтера — бросит исключение, тогда покажем диалог
                return dialog;
            }
            catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException) { }
        }
        return dialog.ShowDialog() == true ? dialog : null;
    }

    /// <summary>Имя принтера для вида документа (пусто — принтер Windows по умолчанию).</summary>
    public static string? PrinterFor(Printing.PrinterKind kind)
    {
        var own = kind switch
        {
            Printing.PrinterKind.Label => App.Ui.Print.LabelPrinterName,
            Printing.PrinterKind.Receipt => App.Ui.Print.ReceiptPrinterName,
            Printing.PrinterKind.Invoice => App.Ui.Print.InvoicePrinterName,
            _ => null
        };
        return string.IsNullOrWhiteSpace(own) ? App.Ui.PrinterName : own;
    }

    private static PrintQueue? FindQueue(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var server = new LocalPrintServer();
            return server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
                .FirstOrDefault(q => string.Equals(q.FullName, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException)
        {
            return null; // принтер удалён — печатаем на принтер по умолчанию
        }
    }
}
