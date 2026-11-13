using MiniTrade.Core.Domain;
using MiniTrade.Desktop.Infrastructure.Printing;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Печать продажи: товарный чек (A4 или чековая лента) и расходная накладная — по настройкам вкладки «Печать».</summary>
public static class SaleReceiptPrinter
{
    public static void Print(Sale sale, CompanySettings company)
    {
        var o = App.Ui.Print.Receipt;
        DocumentPrinter.Print($"{L.T(string.IsNullOrWhiteSpace(o.Title) ? "Товарный чек" : o.Title.Trim())} {sale.Number}",
            PrintDocuments.Receipt(sale, company, o), o.Copies, PrinterKind.Receipt);
    }

    public static void PrintInvoice(Sale sale, CompanySettings company)
    {
        var o = App.Ui.Print.Invoice;
        DocumentPrinter.Print($"{L.T(string.IsNullOrWhiteSpace(o.Title) ? "Расходная накладная" : o.Title.Trim())} {sale.Number}",
            PrintDocuments.Invoice(sale, company, o), o.Copies, PrinterKind.Invoice);
    }

    public static void PrintLabels(IEnumerable<(Product Product, int Copies)> items, CompanySettings company) =>
        DocumentPrinter.Print(L.T("Этикетки товаров"), PrintDocuments.Labels(items, company, App.Ui.Print.Label), 1, PrinterKind.Label);
}
