using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Domain;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Общая логика набора строк документа: выбор товара, штрихкод, итог.</summary>
public abstract partial class DocumentEditorBase(IDialogService dialogs, ICatalogDialogs catalogDialogs) : PageViewModel(dialogs)
{
    /// <summary>Модальные окна справочников (добавить/изменить прямо из документа).</summary>
    protected ICatalogDialogs CatalogDialogs { get; } = catalogDialogs;

    public ObservableCollection<Product> Products { get; } = new();
    public ObservableCollection<DocumentLineViewModel> Lines { get; } = new();

    [ObservableProperty] private Product? _selectedProduct;
    [ObservableProperty] private decimal _newQuantity = 1;
    [ObservableProperty] private decimal _newPrice;
    [ObservableProperty] private string? _barcode;
    [ObservableProperty] private decimal _total;

    /// <summary>Цена по умолчанию для выбранного товара (продажная или закупочная).</summary>
    protected abstract decimal DefaultPrice(Product product);

    partial void OnSelectedProductChanged(Product? value)
    {
        if (value is not null) NewPrice = DefaultPrice(value);
    }

    protected void SetProducts(IEnumerable<Product> products)
    {
        Products.Clear();
        foreach (var p in products) Products.Add(p);
    }

    [RelayCommand]
    private void AddLine()
    {
        if (SelectedProduct is null) { Dialogs.Error(L.T("Выберите товар.")); return; }
        if (NewQuantity <= 0) { Dialogs.Error(L.T("Количество должно быть больше нуля.")); return; }
        AddOrMerge(SelectedProduct, NewQuantity, NewPrice);
        // Поля добавления очищаются для следующего товара
        SelectedProduct = null;
        NewQuantity = 1;
        NewPrice = 0;
        Barcode = "";
    }

    [RelayCommand]
    private async Task AddByBarcodeAsync()
    {
        var code = Barcode?.Trim();
        if (string.IsNullOrEmpty(code)) return;
        var product = Products.FirstOrDefault(p => string.Equals(p.Barcode, code, StringComparison.OrdinalIgnoreCase))
                      ?? Products.FirstOrDefault(p => string.Equals(p.Sku, code, StringComparison.OrdinalIgnoreCase));
        if (product is null)
        {
            // Неизвестный штрихкод — сразу предлагаем завести товар с этим кодом
            if (!Dialogs.Confirm(L.F("Товар со штрихкодом/артикулом «{0}» не найден.\nСоздать новый товар?", code))) return;
            var created = await CatalogDialogs.EditAsync(Products, new Product { Barcode = code });
            if (created?.Saved is not { } p) return;
            product = p;
        }
        AddOrMerge(product, 1, DefaultPrice(product));
        Barcode = "";
    }

    [RelayCommand]
    private async Task AddProductAsync()
    {
        var r = await CatalogDialogs.EditAsync(Products);
        if (r?.Saved is { } p) SelectedProduct = p;
    }

    [RelayCommand]
    private async Task EditProductAsync()
    {
        if (SelectedProduct is null) { Dialogs.Error(L.T("Выберите товар.")); return; }
        var r = await CatalogDialogs.EditAsync(Products, SelectedProduct);
        if (r is not null) SelectedProduct = r.Saved;
    }

    [RelayCommand]
    private void RemoveLine(DocumentLineViewModel? line)
    {
        if (line is null) return;
        line.Changed -= OnLineChanged;
        Lines.Remove(line);
        Recalculate();
    }

    [RelayCommand]
    protected void ClearLines()
    {
        foreach (var l in Lines) l.Changed -= OnLineChanged;
        Lines.Clear();
        Recalculate();
    }

    /// <summary>Строка добавлена (или к ней прибавлено количество) — представление ставит фокус на её количество.</summary>
    public event EventHandler<DocumentLineViewModel>? LineAdded;

    protected void AddOrMerge(Product product, decimal qty, decimal price)
    {
        var existing = Lines.FirstOrDefault(l => l.Product.Id == product.Id && l.Price == price);
        if (existing is not null)
        {
            existing.Quantity += qty;
            LineAdded?.Invoke(this, existing);
            return;
        }
        var line = new DocumentLineViewModel(product, qty, price);
        line.Changed += OnLineChanged;
        Lines.Add(line);
        Recalculate();
        LineAdded?.Invoke(this, line);
    }

    private void OnLineChanged(object? sender, EventArgs e) => Recalculate();

    protected void Recalculate() => Total = Lines.Sum(l => l.Amount);
}
