using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniTrade.Desktop.Infrastructure.Printing;

/// <summary>Бумага товарного чека: лист A4 или чековая лента термопринтера.</summary>
public enum ReceiptFormat { A4, Tape80, Tape58 }

/// <summary>Настройки товарного чека.</summary>
public partial class ReceiptOptions : ObservableObject
{
    [ObservableProperty] private ReceiptFormat _format = ReceiptFormat.A4;
    [ObservableProperty] private string _title = "Товарный чек";
    [ObservableProperty] private double _fontSize = 12;
    [ObservableProperty] private bool _showCompanyDetails = true;
    [ObservableProperty] private bool _showCustomer = true;
    [ObservableProperty] private bool _showSku;
    [ObservableProperty] private bool _showSignature = true;
    [ObservableProperty] private bool _showFooter = true;
    [ObservableProperty] private int _copies = 1;
}

/// <summary>Настройки расходной накладной (A4).</summary>
public partial class InvoiceOptions : ObservableObject
{
    [ObservableProperty] private string _title = "Расходная накладная";
    [ObservableProperty] private double _fontSize = 12;
    [ObservableProperty] private bool _landscape;
    [ObservableProperty] private bool _showCompanyDetails = true;
    [ObservableProperty] private bool _showSku = true;
    [ObservableProperty] private bool _showBarcode;
    [ObservableProperty] private bool _showAmountInWords = true;
    [ObservableProperty] private bool _showComment = true;
    [ObservableProperty] private bool _showSignatures = true;
    [ObservableProperty] private int _copies = 2;
}

/// <summary>Настройки этикетки (ценника) товара.</summary>
public partial class LabelOptions : ObservableObject
{
    [ObservableProperty] private double _widthMm = 58;
    [ObservableProperty] private double _heightMm = 40;
    [ObservableProperty] private bool _showCompany;
    [ObservableProperty] private bool _showName = true;
    [ObservableProperty] private bool _showSku = true;
    [ObservableProperty] private bool _showPrice = true;
    [ObservableProperty] private bool _showBarcode = true;
    [ObservableProperty] private bool _showBarcodeText = true;
    [ObservableProperty] private bool _showDate;
    [ObservableProperty] private double _nameFontSize = 9;
    [ObservableProperty] private double _priceFontSize = 16;
}

/// <summary>Все настройки печати этого компьютера (часть ui.json).</summary>
public sealed class PrintOptions
{
    /// <summary>Показывать окно предпросмотра перед печатью.</summary>
    public bool ShowPreview { get; set; } = true;
    /// <summary>Принтер этикеток (пусто — тот же, что для документов).</summary>
    public string? LabelPrinterName { get; set; }
    /// <summary>Принтер чеков (пусто — основной).</summary>
    public string? ReceiptPrinterName { get; set; }
    /// <summary>Принтер накладных (пусто — основной).</summary>
    public string? InvoicePrinterName { get; set; }
    public ReceiptOptions Receipt { get; set; } = new();
    public InvoiceOptions Invoice { get; set; } = new();
    public LabelOptions Label { get; set; } = new();
}
