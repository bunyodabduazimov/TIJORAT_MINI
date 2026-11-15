using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Строка состава техкарты.</summary>
public partial class RecipeLineViewModel(Product product, decimal quantity) : ObservableObject
{
    public Product Product { get; } = product;
    [ObservableProperty] private decimal _quantity = quantity;
}

/// <summary>Производство: выпуск продукции по техкартам и реестр документов.</summary>
public partial class ProductionViewModel : DocumentEditorBase
{
    private readonly ProductionService _production;
    private readonly WarehouseService _stockService;
    private readonly CatalogService _catalog;
    private Dictionary<int, (decimal Quantity, decimal AvgCost)> _stock = new();

    private readonly SettingsService _settings;

    public ProductionViewModel(ProductionService production, WarehouseService warehouse, CatalogService catalog, SettingsService settings,
        IDialogService dialogs, ICatalogDialogs catalogDialogs) : base(dialogs, catalogDialogs)
    {
        _settings = settings;
        _production = production;
        _stockService = warehouse;
        _catalog = catalog;

        OrdersView = CollectionViewSource.GetDefaultView(Orders);
        OrdersView.Filter = o => o is ProductionOrder d && Matches(OrderSearch, d.Number, d.Product?.Name, d.Warehouse?.Name, d.Comment);

        RecipesView = CollectionViewSource.GetDefaultView(Recipes);
        RecipesView.Filter = o => o is Recipe r && Matches(RecipeSearch, r.Name, r.Product?.Name, r.Comment);

        // Новые строки материалов — сразу с остатком склада
        Lines.CollectionChanged += (_, e) =>
        {
            foreach (var l in e.NewItems?.OfType<DocumentLineViewModel>() ?? []) l.StockFrom = StockOf(l.Product.Id);
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Total)) OnPropertyChanged(nameof(EstimatedUnitCost));
        };
    }

    public override string Title => L.T("Производство");

    public ObservableCollection<Warehouse> Warehouses { get; } = new();
    public ObservableCollection<Recipe> Recipes { get; } = new();
    public ICollectionView RecipesView { get; }
    public ObservableCollection<Recipe> ActiveRecipes { get; } = new();
    public ObservableCollection<ProductionOrder> Orders { get; } = new();
    public ICollectionView OrdersView { get; }
    public ObservableCollection<ProductionItem> OrderItems { get; } = new();

    [ObservableProperty] private int _selectedTab;

    /// <summary>Цена строки материала — себестоимость на складе (для оценки стоимости выпуска).</summary>
    protected override decimal DefaultPrice(Product product) =>
        _stock.TryGetValue(product.Id, out var s) && s.AvgCost != 0 ? s.AvgCost : product.PurchasePrice;

    private decimal StockOf(int productId) => _stock.TryGetValue(productId, out var s) ? s.Quantity : 0;

    private static bool Matches(string? search, params string?[] fields) =>
        string.IsNullOrWhiteSpace(search) || fields.Any(f => f?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true);

    public override Task LoadAsync() => RunAsync(async () =>
    {
        SetProducts(await _catalog.GetAllAsync<Product>(activeOnly: true));
        Fill(Warehouses, await _catalog.GetAllAsync<Warehouse>(true));
        Warehouse ??= Warehouses.FirstOrDefault();
        await LoadRecipesCore();
        await LoadStockAsync();
        await LoadOrdersCore();
    });

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    // ================= Выпуск продукции =================

    [ObservableProperty] private Warehouse? _warehouse;
    [ObservableProperty] private Recipe? _recipe;
    [ObservableProperty] private Product? _outputProduct;
    [ObservableProperty] private decimal _outputQuantity = 1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EstimatedUnitCost))] private decimal _extraCost;
    [ObservableProperty] private DateTime? _orderDate = DateTime.Today;
    [ObservableProperty] private string? _orderComment;
    [ObservableProperty] private bool _isOrderEditorOpen;

    /// <summary>Ожидаемая себестоимость единицы продукции.</summary>
    public decimal EstimatedUnitCost => OutputQuantity > 0 ? Money.Round((Total + ExtraCost) / OutputQuantity) : 0;

    partial void OnWarehouseChanged(Warehouse? value) => _ = RunAsync(LoadStockAsync);

    partial void OnRecipeChanged(Recipe? value)
    {
        if (value is null) return;
        OutputProduct = Products.FirstOrDefault(p => p.Id == value.ProductId) ?? value.Product;
        FillFromRecipe();
    }

    /// <summary>Выбрана продукция — если для неё есть техкарта, материалы заполняются по ней.</summary>
    partial void OnOutputProductChanged(Product? value)
    {
        var recipe = value is null ? null : ActiveRecipes.FirstOrDefault(r => r.ProductId == value.Id);
        if (recipe is null && Recipe is not null) ClearLines(); // убираем материалы прежней техкарты
        Recipe = recipe;
    }

    partial void OnOutputQuantityChanged(decimal value)
    {
        OnPropertyChanged(nameof(EstimatedUnitCost));
        if (Recipe is not null) FillFromRecipe();
    }

    /// <summary>Материалы по техкарте, пересчитанные на нужное количество продукции.</summary>
    private void FillFromRecipe()
    {
        if (Recipe is null || OutputQuantity <= 0 || Recipe.OutputQuantity <= 0) return;
        var k = OutputQuantity / Recipe.OutputQuantity;
        ClearLines();
        foreach (var item in Recipe.Items)
        {
            var product = Products.FirstOrDefault(p => p.Id == item.ProductId) ?? item.Product;
            if (product is null) continue;
            AddOrMerge(product, Money.RoundQty(item.Quantity * k), DefaultPrice(product));
        }
    }

    private async Task LoadStockAsync()
    {
        _stock = Warehouse is null ? new() : await _stockService.GetWarehouseStockAsync(Warehouse.Id);
        foreach (var l in Lines)
        {
            l.StockFrom = StockOf(l.Product.Id);
            l.Price = DefaultPrice(l.Product);
        }
    }

    [RelayCommand]
    private void CreateOrder()
    {
        Warehouse ??= Warehouses.FirstOrDefault();
        IsOrderEditorOpen = true;
    }

    [RelayCommand]
    private void CloseOrderEditor()
    {
        if (Lines.Count > 0 && !Dialogs.Confirm(L.T("Закрыть без сохранения? Добавленные позиции будут удалены."))) return;
        ResetOrderEditor();
    }

    private void ResetOrderEditor()
    {
        Recipe = null;
        ClearLines();
        OutputProduct = null;
        OutputQuantity = 1;
        ExtraCost = 0;
        OrderComment = null;
        OrderDate = DateTime.Today;
        EditingOrderId = 0;
        IsOrderEditorOpen = false;
    }

    /// <summary>Id изменяемого документа (0 — новый выпуск).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OrderEditorTitle))] private int _editingOrderId;
    private string? _editingOrderNumber;
    public string OrderEditorTitle => EditingOrderId == 0 ? L.T("Новый выпуск продукции") : L.F("Изменение выпуска {0}", _editingOrderNumber);

    [RelayCommand]
    private Task EditOrderAsync(ProductionOrder? order) => order is null ? Task.CompletedTask : OpenOrderInEditorAsync(order, edit: true);

    [RelayCommand]
    private Task CopyOrderAsync(ProductionOrder? order) => order is null ? Task.CompletedTask : OpenOrderInEditorAsync(order, edit: false);

    /// <summary>Открывает редактор, заполненный данными документа (для изменения или как копию).</summary>
    private Task OpenOrderInEditorAsync(ProductionOrder order, bool edit) => RunAsync(async () =>
    {
        var items = await _production.GetOrderItemsAsync(order.Id);
        Warehouse = Warehouses.FirstOrDefault(w => w.Id == order.WarehouseId) ?? Warehouses.FirstOrDefault();
        OutputProduct = Products.FirstOrDefault(p => p.Id == order.ProductId) ?? order.Product;
        OutputQuantity = order.Quantity;
        ClearLines();
        foreach (var i in items)
        {
            var product = Products.FirstOrDefault(p => p.Id == i.ProductId) ?? i.Product;
            if (product is not null) AddOrMerge(product, i.Quantity, DefaultPrice(product));
        }
        ExtraCost = order.ExtraCost;
        OrderComment = order.Comment;
        OrderDate = edit ? order.Date.Date : DateTime.Today;
        _editingOrderNumber = order.Number;
        EditingOrderId = edit ? order.Id : 0;
        OnPropertyChanged(nameof(OrderEditorTitle));
        IsOrderDetailOpen = false;
        IsOrderEditorOpen = true;
    });

    [RelayCommand]
    private async Task DeleteOrderAsync(ProductionOrder? order)
    {
        if (order is null) return;
        if (!Dialogs.Confirm(L.F("Удалить выпуск {0}? Продукция будет списана, материалы вернутся на склад.", order.Number))) return;
        if (!await RunAsync(() => _production.CancelAsync(order.Id))) return;
        IsOrderDetailOpen = false;
        await LoadAsync();
    }

    [RelayCommand]
    private Task PrintOrderAsync(ProductionOrder? order) => order is null ? Task.CompletedTask : RunAsync(async () =>
    {
        var items = await _production.GetOrderItemsAsync(order.Id);
        var title = L.F("Выпуск продукции {0}", order.Number);
        var subtitle = L.F("{0:dd.MM.yyyy HH:mm} · {1} · {2}: {3:0.###} {4}", order.Date, order.Warehouse?.Name,
            order.Product?.Name, order.Quantity, order.Product?.Unit);
        var rows = items.Select(i => new List<string> { i.Product?.Name ?? "", $"{i.Quantity:0.###} {i.Product?.Unit}", $"{i.UnitCost:N2}", $"{i.Amount:N2}" });
        var totals = new List<string> { L.F("Материалы: {0}", Money.Format(order.MaterialsCost)) };
        if (order.ExtraCost != 0) totals.Add(L.F("Доп. расходы: {0}", Money.Format(order.ExtraCost)));
        totals.Add(L.F("Себестоимость выпуска: {0}  (за ед.: {1:N2})", Money.Format(order.TotalCost), order.UnitCost));
        if (!string.IsNullOrWhiteSpace(order.Comment)) totals.Add(order.Comment!);
        var doc = Infrastructure.Printing.PrintDocuments.TableReport(title, subtitle, await _settings.GetCompanyAsync(),
            [(L.T("Материал"), new System.Windows.GridLength(1, System.Windows.GridUnitType.Star), false),
             (L.T("Количество"), new System.Windows.GridLength(90), true),
             (L.T("Себест. ед."), new System.Windows.GridLength(90), true),
             (L.T("Сумма"), new System.Windows.GridLength(100), true)],
            rows, totals);
        Infrastructure.Printing.DocumentPrinter.Print(title, doc);
    });

    [RelayCommand]
    private async Task PostOrderAsync()
    {
        if (Warehouse is null) { Dialogs.Error(L.T("Выберите склад.")); return; }
        if (OutputProduct is null) { Dialogs.Error(L.T("Выберите продукцию.")); return; }
        if (OutputQuantity <= 0) { Dialogs.Error(L.T("Количество продукции должно быть больше нуля.")); return; }
        if (Lines.Count == 0) { Dialogs.Error(L.T("Добавьте материалы для производства.")); return; }

        var shortages = Lines.GroupBy(l => l.Product)
            .Select(g => (Product: g.Key, Qty: g.Sum(x => x.Quantity), Stock: StockOf(g.Key.Id)))
            .Where(x => x.Qty > x.Stock && !x.Product.IsService)
            .Select(x => $"• {x.Product.Name}: {x.Qty:0.###} > {x.Stock:0.###} {x.Product.Unit}")
            .ToList();
        if (shortages.Count > 0
            && !Dialogs.Confirm(L.F("Недостаточно материалов на складе «{0}»:\n{1}\nВсё равно провести?", Warehouse.Name, string.Join("\n", shortages))))
            return;
        if (!Dialogs.Confirm(L.F("Выпустить «{0}» — {1:0.###} {2}?\nМатериалов: {3} поз.", OutputProduct.Name, OutputQuantity, OutputProduct.Unit, Lines.Count)))
            return;

        var date = (OrderDate ?? DateTime.Today).Date + DateTime.Now.TimeOfDay;
        var request = new ProductionRequest(Warehouse.Id, OutputProduct.Id, OutputQuantity,
            Lines.Select(l => new MaterialLine(l.Product.Id, l.Quantity)).ToList(), ExtraCost, OrderComment, date, Recipe?.Id);
        ProductionOrder? doc = null;
        var replaceId = EditingOrderId == 0 ? (int?)null : EditingOrderId;
        var ok = await RunAsync(async () => doc = await _production.ProduceAsync(request, default, replaceId));
        if (!ok || doc is null) return;
        Dialogs.Info(L.F("Производство {0} проведено. Себестоимость единицы: {1:N2}", doc.Number, doc.UnitCost));
        ResetOrderEditor();
        await LoadAsync();
    }

    // ================= Реестр документов =================

    [ObservableProperty] private DateTime? _periodFrom = DateTime.Today.AddMonths(-1);
    [ObservableProperty] private DateTime? _periodTo = DateTime.Today;
    [ObservableProperty] private string? _orderSearch;
    [ObservableProperty] private ProductionOrder? _selectedOrder;
    [ObservableProperty] private bool _isOrderDetailOpen;
    [ObservableProperty] private int _ordersCount;
    [ObservableProperty] private decimal _ordersCost;

    partial void OnOrderSearchChanged(string? value) => RefreshOrders();

    partial void OnSelectedOrderChanged(ProductionOrder? value) => _ = RunAsync(async () =>
        Fill(OrderItems, value is null ? [] : await _production.GetOrderItemsAsync(value.Id)));

    private void RefreshOrders()
    {
        OrdersView.Refresh();
        var visible = OrdersView.Cast<ProductionOrder>().ToList();
        OrdersCount = visible.Count;
        OrdersCost = visible.Sum(o => o.TotalCost);
    }

    private async Task LoadOrdersCore()
    {
        Fill(Orders, await _production.GetOrdersAsync(PeriodFrom, PeriodTo));
        RefreshOrders();
    }

    [RelayCommand]
    private Task LoadOrdersAsync() => RunAsync(LoadOrdersCore);

    [RelayCommand]
    private Task SetPeriodAsync(string? period)
    {
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        (PeriodFrom, PeriodTo) = period switch
        {
            "today" => ((DateTime?)today, (DateTime?)today),
            "week" => (today.AddDays(-6), today),
            "month" => (monthStart, today),
            "prevmonth" => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            "year" => (new DateTime(today.Year, 1, 1), today),
            "all" => (null, null),
            _ => (PeriodFrom, PeriodTo)
        };
        return RunAsync(LoadOrdersCore);
    }

    [RelayCommand]
    private void OpenOrderDetail()
    {
        if (SelectedOrder is not null) IsOrderDetailOpen = true;
    }

    [RelayCommand]
    private void CloseOrderDetail() => IsOrderDetailOpen = false;

    [RelayCommand]
    private async Task CancelOrderAsync()
    {
        if (SelectedOrder is not { } doc) return;
        if (!Dialogs.Confirm(L.F("Отменить производство {0}? Продукция будет списана, материалы вернутся на склад.", doc.Number))) return;
        if (!await RunAsync(() => _production.CancelAsync(doc.Id))) return;
        IsOrderDetailOpen = false;
        await LoadAsync();
    }

    // ================= Техкарты =================

    [ObservableProperty] private string? _recipeSearch;
    [ObservableProperty] private Recipe? _selectedRecipe;
    [ObservableProperty] private bool _isRecipeEditorOpen;
    [ObservableProperty] private int _editingRecipeId;
    [ObservableProperty] private string? _recipeName;
    [ObservableProperty] private Product? _recipeProduct;
    [ObservableProperty] private decimal _recipeOutput = 1;
    [ObservableProperty] private string? _recipeComment;
    [ObservableProperty] private bool _recipeIsActive = true;
    [ObservableProperty] private Product? _recipeMaterial;
    [ObservableProperty] private decimal _recipeMaterialQuantity = 1;
    public ObservableCollection<RecipeLineViewModel> RecipeLines { get; } = new();

    public string RecipeEditorTitle => EditingRecipeId == 0 ? L.T("Новая техкарта") : L.T("Техкарта");
    partial void OnEditingRecipeIdChanged(int value) => OnPropertyChanged(nameof(RecipeEditorTitle));

    partial void OnRecipeSearchChanged(string? value) => RecipesView.Refresh();

    partial void OnRecipeProductChanged(Product? value)
    {
        if (value is not null && string.IsNullOrWhiteSpace(RecipeName)) RecipeName = value.Name;
    }

    private async Task LoadRecipesCore()
    {
        var all = await _production.GetRecipesAsync();
        Fill(Recipes, all);
        var current = Recipe;
        Fill(ActiveRecipes, all.Where(r => r.IsActive));
        Recipe = current is null ? null : ActiveRecipes.FirstOrDefault(r => r.Id == current.Id);
        RecipesView.Refresh();
    }

    [RelayCommand]
    private void CreateRecipe() => OpenRecipe(null);

    [RelayCommand]
    private void EditRecipe(Recipe? recipe)
    {
        recipe ??= SelectedRecipe;
        if (recipe is not null) OpenRecipe(recipe);
    }

    /// <summary>Новая техкарта на основе выбранной (состав копируется, сохраняется как новая).</summary>
    [RelayCommand]
    private void CopyRecipe(Recipe? recipe)
    {
        recipe ??= SelectedRecipe;
        if (recipe is null) return;
        OpenRecipe(recipe);
        EditingRecipeId = 0;
    }

    private void OpenRecipe(Recipe? r)
    {
        EditingRecipeId = r?.Id ?? 0;
        RecipeName = r?.Name;
        RecipeProduct = r is null ? null : Products.FirstOrDefault(p => p.Id == r.ProductId) ?? r.Product;
        RecipeOutput = r?.OutputQuantity ?? 1;
        RecipeComment = r?.Comment;
        RecipeIsActive = r?.IsActive ?? true;
        RecipeLines.Clear();
        foreach (var i in r?.Items ?? [])
        {
            var p = Products.FirstOrDefault(x => x.Id == i.ProductId) ?? i.Product;
            if (p is not null) RecipeLines.Add(new RecipeLineViewModel(p, i.Quantity));
        }
        RecipeMaterial = null;
        RecipeMaterialQuantity = 1;
        IsRecipeEditorOpen = true;
    }

    [RelayCommand]
    private void AddRecipeLine()
    {
        if (RecipeMaterial is null) { Dialogs.Error(L.T("Выберите товар.")); return; }
        if (RecipeMaterialQuantity <= 0) { Dialogs.Error(L.T("Количество должно быть больше нуля.")); return; }
        var existing = RecipeLines.FirstOrDefault(l => l.Product.Id == RecipeMaterial.Id);
        if (existing is not null) existing.Quantity += RecipeMaterialQuantity;
        else RecipeLines.Add(new RecipeLineViewModel(RecipeMaterial, RecipeMaterialQuantity));
        RecipeMaterial = null;
        RecipeMaterialQuantity = 1;
    }

    [RelayCommand]
    private void RemoveRecipeLine(RecipeLineViewModel? line)
    {
        if (line is not null) RecipeLines.Remove(line);
    }

    [RelayCommand]
    private void CloseRecipeEditor() => IsRecipeEditorOpen = false;

    // Создание / изменение товара прямо из полей выбора (как в документах склада)
    private async Task<Product?> EditProductCoreAsync(Product? product)
    {
        var r = await CatalogDialogs.EditAsync(Products, product);
        return r?.Saved;
    }

    [RelayCommand]
    private async Task AddRecipeMaterialAsync() { if (await EditProductCoreAsync(null) is { } p) RecipeMaterial = p; }

    [RelayCommand]
    private async Task EditRecipeMaterialAsync() { if (RecipeMaterial is not null && await EditProductCoreAsync(RecipeMaterial) is { } p) RecipeMaterial = p; }

    [RelayCommand]
    private async Task AddRecipeProductAsync() { if (await EditProductCoreAsync(null) is { } p) RecipeProduct = p; }

    [RelayCommand]
    private async Task EditRecipeProductAsync() { if (RecipeProduct is not null && await EditProductCoreAsync(RecipeProduct) is { } p) RecipeProduct = p; }

    [RelayCommand]
    private async Task AddOutputProductAsync() { if (await EditProductCoreAsync(null) is { } p) OutputProduct = p; }

    [RelayCommand]
    private async Task EditOutputProductAsync() { if (OutputProduct is not null && await EditProductCoreAsync(OutputProduct) is { } p) OutputProduct = p; }

    [RelayCommand]
    private async Task SaveRecipeAsync()
    {
        if (RecipeProduct is null) { Dialogs.Error(L.T("Выберите продукцию.")); return; }
        var recipe = new Recipe
        {
            // Название техкарты = название продукции (отдельное поле не нужно)
            Id = EditingRecipeId, Name = RecipeProduct.Name, ProductId = RecipeProduct.Id, OutputQuantity = RecipeOutput,
            Comment = RecipeComment, IsActive = RecipeIsActive
        };
        var lines = RecipeLines.Select(l => new MaterialLine(l.Product.Id, l.Quantity)).ToList();
        if (!await RunAsync(() => _production.SaveRecipeAsync(recipe, lines))) return;
        IsRecipeEditorOpen = false;
        await RunAsync(LoadRecipesCore);
    }

    [RelayCommand]
    private async Task DeleteRecipeAsync(Recipe? recipe)
    {
        if ((recipe ?? SelectedRecipe) is not { } r) return;
        if (!Dialogs.Confirm(L.F("Удалить техкарту «{0}»? Проведённые документы производства сохранятся.", r.Name))) return;
        if (!await RunAsync(() => _production.DeleteRecipeAsync(r.Id))) return;
        await RunAsync(LoadRecipesCore);
    }

    /// <summary>Открыть выпуск по выбранной техкарте.</summary>
    [RelayCommand]
    private void ProduceByRecipe(Recipe? recipe)
    {
        recipe ??= SelectedRecipe;
        if (recipe is null) return;
        if (!recipe.IsActive) { Dialogs.Error(L.T("Техкарта не активна.")); return; }
        SelectedTab = 0;
        CreateOrder();
        Recipe = ActiveRecipes.FirstOrDefault(r => r.Id == recipe.Id);
    }
}
