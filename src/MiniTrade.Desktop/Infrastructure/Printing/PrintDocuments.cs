using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;

namespace MiniTrade.Desktop.Infrastructure.Printing;

/// <summary>
/// Печатные формы: товарный чек (A4 или чековая лента), расходная накладная (A4), этикетки товаров.
/// Возвращают документ, который одинаково показывается в предпросмотре и отправляется на принтер.
/// </summary>
public static class PrintDocuments
{
    /// <summary>Пикселей WPF (1/96 дюйма) в миллиметре.</summary>
    public const double Mm = 96 / 25.4;
    public static readonly Size A4 = new(210 * Mm, 297 * Mm);
    private static readonly FontFamily Font = new("Segoe UI");

    // ───────────────────────────── Товарный чек ─────────────────────────────

    public static IDocumentPaginatorSource Receipt(Sale sale, CompanySettings company, ReceiptOptions o) =>
        o.Format == ReceiptFormat.A4 ? ReceiptA4(sale, company, o) : ReceiptTape(sale, company, o);

    private static string DocTitle(string? title, string fallback) => L.T(string.IsNullOrWhiteSpace(title) ? fallback : title.Trim());

    private static string CompanyDetails(CompanySettings c, string separator) =>
        string.Join(separator, new[] { c.TaxId is { Length: > 0 } inn ? L.F("ИНН {0}", inn) : null, c.Address, c.Phone, c.Email }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    private static FlowDocument A4Document(double fontSize, bool landscape = false) => new()
    {
        FontFamily = Font, FontSize = fontSize, Foreground = Brushes.Black, Background = Brushes.White,
        PagePadding = new Thickness(15 * Mm, 12 * Mm, 15 * Mm, 12 * Mm), ColumnWidth = landscape ? A4.Height : A4.Width,
        PageWidth = landscape ? A4.Height : A4.Width, PageHeight = landscape ? A4.Width : A4.Height
    };

    /// <summary>Ширина текста страницы A4 (без полей).</summary>
    private static double ContentWidth(FlowDocument doc) => doc.PageWidth - doc.PagePadding.Left - doc.PagePadding.Right;

    private static FlowDocument ReceiptA4(Sale sale, CompanySettings company, ReceiptOptions o)
    {
        var fs = Math.Clamp(o.FontSize, 8, 20);
        var doc = A4Document(fs);
        var title = DocTitle(o.Title, "Товарный чек");

        doc.Blocks.Add(P(company.Name, fs + 1, bold: true));
        if (o.ShowCompanyDetails && CompanyDetails(company, " · ") is { Length: > 0 } details)
            doc.Blocks.Add(P(details, fs - 2, muted: true, top: 2));

        doc.Blocks.Add(P($"{title} № {sale.Number}", fs + 7, bold: true, top: 24, bottom: 4));
        doc.Blocks.Add(P(L.F("от {0:dd.MM.yyyy HH:mm}", sale.Date), fs, muted: true, bottom: 4));
        if (o.ShowCustomer)
            doc.Blocks.Add(P(L.F("Покупатель: {0}", sale.Customer?.Name ?? L.T("Розничный покупатель")), fs, bottom: 4));

        var cols = new List<(string Header, GridLength Width, bool Right)> { ("№", new(36), false), (L.T("Товар"), new(1, GridUnitType.Star), false) };
        if (o.ShowSku) cols.Add((L.T("Артикул"), new(100), false));
        cols.AddRange([(L.T("Кол-во"), new(80), true), (L.T("Цена"), new(95), true), (L.T("Сумма"), new(105), true)]);
        var n = 0;
        doc.Blocks.Add(ItemsTable(cols, fs, sale.Items.Select(i =>
        {
            var cells = new List<string> { (++n).ToString(), i.Product?.Name ?? "" };
            if (o.ShowSku) cells.Add(i.Product?.Sku ?? "");
            cells.AddRange([$"{i.Quantity:0.###} {i.Product?.Unit}", $"{i.Price:N2}", $"{i.Amount:N2}"]);
            return cells;
        }), ContentWidth(doc), top: 12));

        doc.Blocks.Add(P(L.F("Итого: {0}", Money.Format(sale.Total)), fs + 3, bold: true, top: 12, align: TextAlignment.Right));
        if (sale.ReturnedTotal > 0)
            doc.Blocks.Add(P(L.F("Возвращено: {0}", Money.Format(sale.ReturnedTotal)), fs, muted: true, align: TextAlignment.Right));
        if (!string.IsNullOrWhiteSpace(sale.Comment))
            doc.Blocks.Add(P(sale.Comment, fs, muted: true, top: 12));

        if (o.ShowSignature) doc.Blocks.Add(P(L.T("Продавец") + "  ____________________", fs, top: 40));
        if (o.ShowFooter && !string.IsNullOrWhiteSpace(company.ReceiptFooter))
            doc.Blocks.Add(P(company.ReceiptFooter.Trim(), fs - 1, muted: true, top: 28, align: TextAlignment.Center));
        return doc;
    }

    /// <summary>Чек на ленте термопринтера: одна страница ровно по длине содержимого.</summary>
    private static FixedDocument ReceiptTape(Sale sale, CompanySettings company, ReceiptOptions o)
    {
        var paper = o.Format == ReceiptFormat.Tape58 ? 58 : 80;
        var printable = (o.Format == ReceiptFormat.Tape58 ? 48 : 72) * Mm;
        var fs = Math.Clamp(o.FontSize, 7, 16);
        var title = DocTitle(o.Title, "Товарный чек");

        var panel = new StackPanel { Width = printable };
        void Line(string? text, double size, bool bold = false, TextAlignment align = TextAlignment.Left, double top = 0)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            panel.Children.Add(new TextBlock
            {
                Text = text, FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, TextAlignment = align,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0)
            });
        }
        void Pair(string left, string right, double size, bool bold = false, double top = 0)
        {
            var g = new Grid { Margin = new Thickness(0, top, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var weight = bold ? FontWeights.Bold : FontWeights.Normal;
            g.Children.Add(new TextBlock { Text = left, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap });
            var r = new TextBlock { Text = right, FontSize = size, FontWeight = weight, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(r, 1);
            g.Children.Add(r);
            panel.Children.Add(g);
        }
        void Separator() => panel.Children.Add(new Line
        {
            X1 = 0, X2 = printable, Stroke = Brushes.Black, StrokeThickness = 0.8, StrokeDashArray = [3, 2], Margin = new Thickness(0, 5, 0, 5)
        });

        Line(company.Name, fs + 2, bold: true, align: TextAlignment.Center);
        if (o.ShowCompanyDetails)
            foreach (var d in new[] { company.TaxId is { Length: > 0 } inn ? L.F("ИНН {0}", inn) : null, company.Address, company.Phone })
                Line(d, fs - 1, align: TextAlignment.Center);
        Separator();
        Line($"{title} № {sale.Number}", fs + 1, bold: true, align: TextAlignment.Center);
        Line(sale.Date.ToString("dd.MM.yyyy HH:mm"), fs - 1, align: TextAlignment.Center);
        if (o.ShowCustomer && sale.Customer is { } customer) Line(L.F("Покупатель: {0}", customer.Name), fs - 1, top: 3);
        Separator();
        foreach (var i in sale.Items)
        {
            var name = i.Product?.Name ?? "";
            if (o.ShowSku && !string.IsNullOrWhiteSpace(i.Product?.Sku)) name += $" ({i.Product.Sku})";
            Line(name, fs, top: 2);
            Pair($"  {i.Quantity:0.###} {i.Product?.Unit} × {i.Price:N2}", $"{i.Amount:N2}", fs);
        }
        Separator();
        Pair(L.T("ИТОГО"), Money.Format(sale.Total), fs + 3, bold: true);
        if (sale.ReturnedTotal > 0) Pair(L.T("Возвращено"), Money.Format(sale.ReturnedTotal), fs - 1);
        if (!string.IsNullOrWhiteSpace(sale.Comment)) Line(sale.Comment, fs - 1, top: 4);
        if (o.ShowSignature) Line(L.T("Продавец") + " ________________", fs - 1, top: 14);
        if (o.ShowFooter && !string.IsNullOrWhiteSpace(company.ReceiptFooter))
        {
            Separator();
            Line(company.ReceiptFooter.Trim(), fs - 1, align: TextAlignment.Center);
        }

        var margin = (paper * Mm - printable) / 2;
        return SinglePageDocument(panel, paper * Mm, new Thickness(margin, 3 * Mm, margin, 8 * Mm));
    }

    // ───────────────────────────── Накладная ─────────────────────────────

    public static FlowDocument Invoice(Sale sale, CompanySettings company, InvoiceOptions o)
    {
        var fs = Math.Clamp(o.FontSize, 8, 18);
        var doc = A4Document(fs, o.Landscape);
        var title = DocTitle(o.Title, "Расходная накладная");

        doc.Blocks.Add(P($"{title} № {sale.Number} {L.F("от {0:dd.MM.yyyy}", sale.Date)}", fs + 6, bold: true, bottom: 14));

        // Стороны
        var parties = new Table { CellSpacing = 0 };
        parties.Columns.Add(new TableColumn { Width = new GridLength(110) });
        parties.Columns.Add(new TableColumn());
        var pr = new TableRowGroup();
        void Party(string label, string name, string? details)
        {
            var value = new Paragraph(new Run(name) { FontWeight = FontWeights.SemiBold }) { Margin = new Thickness(0) };
            if (!string.IsNullOrWhiteSpace(details)) value.Inlines.Add(new Run(", " + details) { Foreground = Brushes.DimGray });
            pr.Rows.Add(new TableRow
            {
                Cells =
                {
                    new TableCell(new Paragraph(new Run(label)) { Margin = new Thickness(0), Foreground = Brushes.DimGray }) { Padding = new Thickness(0, 2, 8, 2) },
                    new TableCell(value) { Padding = new Thickness(0, 2, 0, 2) }
                }
            });
        }
        Party(L.T("Поставщик:"), company.Name, o.ShowCompanyDetails ? CompanyDetails(company, ", ") : null);
        Party(L.T("Покупатель:"), sale.Customer?.Name ?? L.T("Розничный покупатель"),
            sale.Customer is { } c ? string.Join(", ", new[] { c.Phone, c.Address }.Where(s => !string.IsNullOrWhiteSpace(s))) : null);
        if (sale.Warehouse is { } w) Party(L.T("Склад:"), w.Name, null);
        parties.RowGroups.Add(pr);
        doc.Blocks.Add(parties);

        var cols = new List<(string Header, GridLength Width, bool Right)> { ("№", new(34), false), (L.T("Наименование"), new(1, GridUnitType.Star), false) };
        if (o.ShowSku) cols.Add((L.T("Артикул"), new(90), false));
        if (o.ShowBarcode) cols.Add((L.T("Штрихкод"), new(115), false));
        cols.AddRange([(L.T("Ед."), new(44), false), (L.T("Кол-во"), new(70), true), (L.T("Цена"), new(90), true), (L.T("Сумма"), new(100), true)]);
        var n = 0;
        doc.Blocks.Add(ItemsTable(cols, fs, sale.Items.Select(i =>
        {
            var cells = new List<string> { (++n).ToString(), i.Product?.Name ?? "" };
            if (o.ShowSku) cells.Add(i.Product?.Sku ?? "");
            if (o.ShowBarcode) cells.Add(i.Product?.Barcode ?? "");
            cells.AddRange([i.Product?.Unit ?? "", $"{i.Quantity:0.###}", $"{i.Price:N2}", $"{i.Amount:N2}"]);
            return cells;
        }), ContentWidth(doc), top: 14, grid: true));

        doc.Blocks.Add(P(L.F("Итого: {0}", Money.Format(sale.Total)), fs + 2, bold: true, top: 10, align: TextAlignment.Right));
        doc.Blocks.Add(P(L.F("Всего наименований {0}, на сумму {1}", sale.Items.Count, Money.Format(sale.Total)), fs, top: 8));
        if (o.ShowAmountInWords) doc.Blocks.Add(P(AmountInWords.Format(sale.Total), fs, bold: true, top: 2));
        if (o.ShowComment && !string.IsNullOrWhiteSpace(sale.Comment)) doc.Blocks.Add(P(L.F("Примечание: {0}", sale.Comment), fs, muted: true, top: 8));

        if (o.ShowSignatures)
        {
            var sign = new Table { Margin = new Thickness(0, 36, 0, 0) };
            sign.Columns.Add(new TableColumn());
            sign.Columns.Add(new TableColumn());
            var row = new TableRow();
            row.Cells.Add(new TableCell(P(L.T("Отпустил") + "  ______________ / ______________ /", fs)));
            row.Cells.Add(new TableCell(P(L.T("Получил") + "  ______________ / ______________ /", fs)));
            sign.RowGroups.Add(new TableRowGroup { Rows = { row } });
            doc.Blocks.Add(sign);
            doc.Blocks.Add(P(L.T("М.П."), fs - 1, muted: true, top: 10));
        }
        return doc;
    }

    // ───────────────────────────── Таблица-отчёт ─────────────────────────────

    /// <summary>Простой отчёт A4: организация, заголовок, подзаголовок, таблица и строки итогов.</summary>
    public static FlowDocument TableReport(string title, string? subtitle, CompanySettings company,
        List<(string Header, GridLength Width, bool Right)> cols, IEnumerable<List<string>> rows, IEnumerable<string> totals, bool landscape = false)
    {
        const double fs = 10;
        var doc = A4Document(fs, landscape);
        doc.Blocks.Add(P(company.Name, fs, muted: true, bottom: 6));
        doc.Blocks.Add(P(title, fs + 6, bold: true, bottom: 2));
        if (!string.IsNullOrWhiteSpace(subtitle)) doc.Blocks.Add(P(subtitle, fs, muted: true, bottom: 6));
        doc.Blocks.Add(ItemsTable(cols, fs, rows, ContentWidth(doc), top: 8, grid: true));
        foreach (var t in totals) doc.Blocks.Add(P(t, fs + 1, bold: true, top: 4, align: TextAlignment.Right));
        doc.Blocks.Add(P(L.F("Сформировано {0:dd.MM.yyyy HH:mm}", DateTime.Now), fs - 1, muted: true, top: 12));
        return doc;
    }

    // ───────────────────────────── Этикетки ─────────────────────────────

    /// <summary>Каждая этикетка — отдельная страница размером с этикетку (для принтеров этикеток).</summary>
    public static FixedDocument Labels(IEnumerable<(Product Product, int Copies)> items, CompanySettings company, LabelOptions o)
    {
        var (w, h) = LabelSize(o);
        var doc = new FixedDocument();
        doc.DocumentPaginator.PageSize = new Size(w, h);
        foreach (var (product, copies) in items)
            for (var i = 0; i < Math.Max(1, copies); i++)
            {
                var page = new FixedPage { Width = w, Height = h, Background = Brushes.White };
                page.Children.Add(Label(product, company, o));
                doc.Pages.Add(new PageContent { Child = page });
            }
        return doc;
    }

    public static (double Width, double Height) LabelSize(LabelOptions o) =>
        (Math.Clamp(o.WidthMm, 15, 200) * Mm, Math.Clamp(o.HeightMm, 10, 200) * Mm);

    public static FrameworkElement Label(Product p, CompanySettings company, LabelOptions o)
    {
        var (w, h) = LabelSize(o);
        var pad = 1.5 * Mm;
        var root = new DockPanel
        {
            Width = w, Height = h, LastChildFill = true, Background = Brushes.White,
            Margin = new Thickness(0)
        };
        TextElement.SetFontFamily(root, Font);
        TextElement.SetForeground(root, Brushes.Black);
        var inner = new DockPanel { Margin = new Thickness(pad), LastChildFill = true, ClipToBounds = true };
        root.Children.Add(inner);

        void Top(UIElement e) { DockPanel.SetDock(e, Dock.Top); inner.Children.Add(e); }
        void Bottom(UIElement e) { DockPanel.SetDock(e, Dock.Bottom); inner.Children.Add(e); }
        var small = Math.Max(5, o.NameFontSize - 2);

        if (o.ShowCompany && !string.IsNullOrWhiteSpace(company.Name))
            Top(new TextBlock { Text = company.Name, FontSize = small, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        if (o.ShowName)
            Top(new TextBlock
            {
                Text = p.Name, FontSize = Math.Max(5, o.NameFontSize), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = Math.Max(5, o.NameFontSize) * 1.35 * 2, TextAlignment = TextAlignment.Center
            });

        var meta = string.Join("   ", new[] { o.ShowSku && !string.IsNullOrWhiteSpace(p.Sku) ? L.F("Арт. {0}", p.Sku) : null,
            o.ShowDate ? DateTime.Today.ToString("dd.MM.yyyy") : null }.Where(s => s is not null));
        if (meta.Length > 0) Top(new TextBlock { Text = meta, FontSize = small, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 1, 0, 0) });

        var code = string.IsNullOrWhiteSpace(p.Barcode) ? p.Sku : p.Barcode;
        var barcode = o.ShowBarcode && Code128.CanEncode(code);
        var price = o.ShowPrice
            ? new TextBlock
            {
                Text = $"{p.SalePrice:N2} {Money.Currency}", FontSize = Math.Max(6, o.PriceFontSize), FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 0, 1)
            }
            : null;

        if (!barcode)
        {
            inner.Children.Add(price ?? new TextBlock());
            return root;
        }
        if (price is not null) Top(price);
        if (o.ShowBarcodeText) Bottom(new TextBlock { Text = code, FontSize = small, TextAlignment = TextAlignment.Center });
        inner.Children.Add(new BarcodeElement(code!) { MinHeight = 4 * Mm, Margin = new Thickness(0, 1, 0, 0) });
        return root;
    }

    // ───────────────────────────── Образцы для предпросмотра ─────────────────────────────

    public static Sale SampleSale()
    {
        Product Prod(string name, string sku, string barcode, string unit, decimal price) =>
            new() { Name = name, Sku = sku, Barcode = barcode, Unit = unit, SalePrice = price };
        var items = new[]
        {
            (Prod(L.T("Чай зелёный, 100 г"), "TEA-100", "4870001234567", "шт", 18.50m), 2m),
            (Prod(L.T("Сахар-песок"), "SUG-01", "4870001234574", "кг", 12m), 1.5m),
            (Prod(L.T("Печенье овсяное"), "COK-07", "4870001234581", "уп", 9.90m), 3m)
        };
        var sale = new Sale
        {
            Number = "000125", Date = DateTime.Now, Comment = L.T("Доставка до 18:00"),
            Customer = new Counterparty { Name = L.T("ООО «Пример»"), Phone = "+992 900 00 00 00" },
            Warehouse = new Warehouse { Name = L.T("Основной склад") },
            Items = items.Select(x => new SaleItem { Product = x.Item1, Quantity = x.Item2, Price = x.Item1.SalePrice, Amount = x.Item1.SalePrice * x.Item2 }).ToList()
        };
        sale.Total = sale.Items.Sum(i => i.Amount);
        return sale;
    }

    public static Product SampleProduct() =>
        new() { Name = L.T("Чай зелёный «Пример», 100 г"), Sku = "TEA-100", Barcode = "4870001234567", Unit = "шт", SalePrice = 18.50m };

    // ───────────────────────────── Общее ─────────────────────────────

    /// <summary>Документ из одной страницы точно по высоте содержимого (чековая лента).</summary>
    private static FixedDocument SinglePageDocument(FrameworkElement content, double width, Thickness margin)
    {
        TextElement.SetFontFamily(content, Font);
        TextElement.SetForeground(content, Brushes.Black);
        content.Measure(new Size(width - margin.Left - margin.Right, double.PositiveInfinity));
        var height = content.DesiredSize.Height + margin.Top + margin.Bottom;
        var page = new FixedPage { Width = width, Height = height, Background = Brushes.White };
        FixedPage.SetLeft(content, margin.Left);
        FixedPage.SetTop(content, margin.Top);
        page.Children.Add(content);
        var doc = new FixedDocument();
        doc.DocumentPaginator.PageSize = new Size(width, height);
        doc.Pages.Add(new PageContent { Child = page });
        return doc;
    }

    private static Paragraph P(string text, double size, bool bold = false, bool muted = false, double top = 0, double bottom = 0,
        TextAlignment align = TextAlignment.Left) =>
        new(new Run(text))
        {
            FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = muted ? Brushes.DimGray : Brushes.Black,
            Margin = new Thickness(0, top, 0, bottom), TextAlignment = align
        };

    private static Table ItemsTable(List<(string Header, GridLength Width, bool Right)> cols, double fs, IEnumerable<List<string>> rows,
        double width, double top = 0, bool grid = false)
    {
        // Колонка «*» — в абсолютную ширину: в FlowDocument звёздочка может сжаться почти до нуля
        var fixedSum = cols.Where(c => !c.Width.IsStar).Sum(c => c.Width.Value);
        var stars = cols.Where(c => c.Width.IsStar).Sum(c => c.Width.Value);
        if (stars > 0)
            cols = cols.Select(c => c.Width.IsStar
                ? (c.Header, new GridLength(Math.Max(120, (width - fixedSum - 2) * c.Width.Value / stars)), c.Right) : c).ToList();
        var table = new Table { CellSpacing = 0, BorderBrush = grid ? Brushes.Black : Brushes.Silver, BorderThickness = new Thickness(grid ? 0.5 : 1, grid ? 0.5 : 1, grid ? 0 : 1, 0), Margin = new Thickness(0, top, 0, 0) };
        foreach (var c in cols) table.Columns.Add(new TableColumn { Width = c.Width });
        var group = new TableRowGroup();
        group.Rows.Add(Row(cols.Select(c => c.Header).ToList(), header: true));
        foreach (var r in rows) group.Rows.Add(Row(r, header: false));
        table.RowGroups.Add(group);
        return table;

        TableRow Row(List<string> cells, bool header)
        {
            var row = new TableRow { Background = header && grid ? new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)) : null };
            for (var i = 0; i < cells.Count; i++)
                row.Cells.Add(new TableCell(new Paragraph(new Run(cells[i]))
                {
                    Margin = new Thickness(0), TextAlignment = cols[i].Right ? TextAlignment.Right : header && grid ? TextAlignment.Center : TextAlignment.Left
                })
                {
                    Padding = grid ? new Thickness(5, 3, 5, 3) : new Thickness(8, 6, 8, 6),
                    BorderBrush = grid ? Brushes.Black : Brushes.Silver,
                    BorderThickness = grid ? new Thickness(0, 0, 0.5, 0.5) : new Thickness(0, 0, 0, 1),
                    FontSize = fs, FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = header && !grid ? Brushes.DimGray : Brushes.Black
                });
            return row;
        }
    }
}

/// <summary>Штрихкод Code 128, растянутый на всю ширину и высоту элемента (с полями тишины).</summary>
public sealed class BarcodeElement(string text) : FrameworkElement
{
    private readonly IReadOnlyList<int> _widths = Code128.Encode(text);

    protected override Size MeasureOverride(Size available) => new(0, 0);

    protected override void OnRender(DrawingContext dc)
    {
        const int quiet = 8;
        var modules = _widths.Sum() + 2 * quiet;
        var unit = ActualWidth / modules;
        var x = quiet * unit;
        for (var i = 0; i < _widths.Count; i++)
        {
            var w = _widths[i] * unit;
            if (i % 2 == 0) dc.DrawRectangle(Brushes.Black, null, new Rect(x, 0, w, ActualHeight));
            x += w;
        }
    }
}
