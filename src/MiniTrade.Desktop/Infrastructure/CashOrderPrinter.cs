using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Печать кассовой операции как приходного / расходного кассового ордера (A4, через окно печати Windows).</summary>
public static class CashOrderPrinter
{
    public static void Print(CashTransaction t, CompanySettings company)
    {
        var title = t.Direction == CashDirection.Income ? L.T("Приходный кассовый ордер") : L.T("Расходный кассовый ордер");
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Foreground = Brushes.Black,
            PagePadding = new Thickness(56), ColumnWidth = double.PositiveInfinity,
            PageWidth = Printing.PrintDocuments.A4.Width, PageHeight = Printing.PrintDocuments.A4.Height
        };

        doc.Blocks.Add(new Paragraph(new Run(company.Name) { FontWeight = FontWeights.SemiBold })
        {
            Margin = new Thickness(0),
        });
        var details = string.Join(" · ", new[] { company.TaxId is { Length: > 0 } inn ? L.F("ИНН {0}", inn) : null, company.Address, company.Phone, company.Email }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (details.Length > 0) doc.Blocks.Add(new Paragraph(new Run(details)) { Foreground = Brushes.DimGray, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });

        doc.Blocks.Add(new Paragraph(new Run($"{title} № {t.DocumentNumber}")) { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 28, 0, 4) });
        doc.Blocks.Add(new Paragraph(new Run(L.F("от {0:dd.MM.yyyy HH:mm}", t.Date))) { Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 18) });

        var table = new Table { CellSpacing = 0, BorderBrush = Brushes.Silver, BorderThickness = new Thickness(1, 1, 1, 0) };
        table.Columns.Add(new TableColumn { Width = new GridLength(200) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var rows = new TableRowGroup();
        void Row(string name, string? value, bool strong = false)
        {
            var r = new TableRow();
            r.Cells.Add(Cell(name, Brushes.DimGray, false));
            r.Cells.Add(Cell(value ?? "—", Brushes.Black, strong));
            rows.Rows.Add(r);
        }
        Row(L.T("Касса"), t.CashRegister?.Name);
        Row(L.T("Статья"), t.Category);
        if (t.CounterpartyName is { } cp) Row(t.Direction == CashDirection.Income ? L.T("Принято от") : L.T("Выдано"), cp);
        if (!string.IsNullOrWhiteSpace(t.Comment)) Row(L.T("Основание / комментарий"), t.Comment);
        Row(L.T("Сумма"), Money.Format(t.Amount), strong: true);
        table.RowGroups.Add(rows);
        doc.Blocks.Add(table);

        // Подписи
        var sign = new Table { Margin = new Thickness(0, 48, 0, 0) };
        sign.Columns.Add(new TableColumn());
        sign.Columns.Add(new TableColumn());
        var sr = new TableRow();
        sr.Cells.Add(new TableCell(new Paragraph(new Run(L.T("Кассир") + "  ____________________"))));
        sr.Cells.Add(new TableCell(new Paragraph(new Run((t.Direction == CashDirection.Income ? L.T("Внёс") : L.T("Получил")) + "  ____________________"))));
        sign.RowGroups.Add(new TableRowGroup { Rows = { sr } });
        doc.Blocks.Add(sign);

        Printing.DocumentPrinter.Print($"{title} {t.DocumentNumber}", doc);
    }

    private static TableCell Cell(string text, Brush color, bool strong) =>
        new(new Paragraph(new Run(text)) { Margin = new Thickness(0) })
        {
            Padding = new Thickness(10, 7, 10, 7), BorderBrush = Brushes.Silver, BorderThickness = new Thickness(0, 0, 0, 1),
            Foreground = color, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, FontSize = strong ? 15 : 13
        };
}
