using CommunityToolkit.Mvvm.ComponentModel;
using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Строка документа (продажа, закупка, перемещение, инвентаризация).</summary>
public partial class DocumentLineViewModel : ObservableObject
{
    public DocumentLineViewModel(Product product, decimal quantity, decimal price, decimal available = 0)
    {
        Product = product;
        _quantity = quantity;
        _price = price;
        Available = available;
        _salePrice = product.SalePrice;
        _wholesalePrice = product.WholesalePrice;
    }

    /// <summary>Цена продажи (в закупке — можно изменить, сохраняется в карточку товара).</summary>
    [ObservableProperty] private decimal _salePrice;

    /// <summary>Оптовая цена продажи (в закупке — можно изменить, сохраняется в карточку товара).</summary>
    [ObservableProperty] private decimal _wholesalePrice;

    /// <summary>Сумма строки для ввода: при изменении пересчитывается цена (сумма / количество).</summary>
    public decimal AmountInput
    {
        get => Amount;
        set
        {
            if (Quantity <= 0 || value < 0) return;
            Price = Math.Round(value / Quantity, 4);
        }
    }

    public Product Product { get; }
    public decimal Available { get; }

    /// <summary>Для перемещения: остаток на складе-отправителе и складе-получателе.</summary>
    [ObservableProperty] private decimal _stockFrom;
    [ObservableProperty] private decimal _stockTo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Amount), nameof(AmountInput), nameof(Difference))]
    private decimal _quantity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Amount), nameof(AmountInput))]
    private decimal _price;

    public decimal Amount => Money.Round(Quantity * Price);

    /// <summary>Для инвентаризации: факт − учёт.</summary>
    public decimal Difference => Quantity - Available;

    public event EventHandler? Changed;

    partial void OnQuantityChanged(decimal value) => Changed?.Invoke(this, EventArgs.Empty);
    partial void OnPriceChanged(decimal value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Строка возврата по позиции продажи.</summary>
public partial class ReturnLineViewModel(SaleItem item) : ObservableObject
{
    public int SaleItemId => item.Id;
    public string Product => item.Product?.Name ?? "";
    public decimal Sold => item.Quantity;
    public decimal Returned => item.ReturnedQuantity;
    public decimal Available => item.Quantity - item.ReturnedQuantity;
    public decimal Price => item.Price;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Amount))]
    private decimal _returnQuantity;

    public decimal Amount => Money.Round(ReturnQuantity * Price);
}
