using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Строка листа инвентаризации: учётный остаток, факт, расхождение в деньгах.</summary>
public partial class InventoryLineViewModel(Product product, decimal book, decimal unitCost) : ObservableObject
{
    public Product Product { get; } = product;
    public decimal Book { get; } = book;
    public decimal UnitCost { get; } = unitCost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Difference), nameof(DifferenceAmount), nameof(HasDifference))]
    private decimal _actual = book < 0 ? 0 : book;

    public decimal Difference => Actual - Book;
    public decimal DifferenceAmount => Money.Round(Difference * UnitCost);
    public bool HasDifference => Difference != 0;

    public event EventHandler? Changed;
    partial void OnActualChanged(decimal value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Склад: остатки, перемещения, инвентаризация, журнал движения.</summary>
public partial class WarehouseViewModel : DocumentEditorBase
{
    public override bool HasUnsavedChanges => base.HasUnsavedChanges || InventoryLines.Any(l => l.HasDifference);

    private readonly WarehouseService _warehouse;
    private readonly CatalogService _catalog;
    private Dictionary<int, (decimal Quantity, decimal AvgCost)> _transferStock = new();

    public WarehouseViewModel(WarehouseService warehouse, CatalogService catalog, IDialogService dialogs, ICatalogDialogs catalogDialogs)
        : base(dialogs, catalogDialogs)
    {
        _warehouse = warehouse;
        _catalog = catalog;

        BalancesView = CollectionViewSource.GetDefaultView(Balances);
        BalancesView.Filter = o => o is StockRow r && MatchBalance(r);

        InventoryView = CollectionViewSource.GetDefaultView(InventoryLines);
        InventoryView.Filter = o => o is InventoryLineViewModel l
            && (!InventoryOnlyDifferences || l.HasDifference)
            && Matches(InventorySearch, l.Product.Name, l.Product.Sku, l.Product.Barcode);

        MovementsView = CollectionViewSource.GetDefaultView(Movements);
        MovementsView.Filter = o => o is StockMovement m
            && Matches(MovementSearch, m.Product?.Name, m.Product?.Sku, m.DocumentNumber);

        TransfersView = CollectionViewSource.GetDefaultView(Transfers);
        TransfersView.Filter = o => o is StockTransfer t
            && (TransferWarehouseFilter is null || t.FromWarehouseId == TransferWarehouseFilter.Id || t.ToWarehouseId == TransferWarehouseFilter.Id)
            && Matches(TransferSearch, t.Number, t.Comment, t.FromWarehouse?.Name, t.ToWarehouse?.Name);

        InventoriesView = CollectionViewSource.GetDefaultView(Inventories);
        InventoriesView.Filter = o => o is InventoryCount c
            && (InventoryWarehouseFilter is null || c.WarehouseId == InventoryWarehouseFilter.Id)
            && Matches(InventoriesSearch, c.Number, c.Comment, c.Warehouse?.Name);

        WriteOffsView = CollectionViewSource.GetDefaultView(WriteOffs);
        WriteOffsView.Filter = o => o is StockWriteOff w
            && (WriteOffWarehouseFilter is null || w.WarehouseId == WriteOffWarehouseFilter.Id)
            && Matches(WriteOffsSearch, w.Number, w.Reason, w.Comment, w.Warehouse?.Name);

        MovementTypes = [new(null, L.T("Все операции")), ..Enum.GetValues<StockMovementType>()
            .Select(t => new MovementTypeOption(t, (string)new EnumNameConverter().Convert(t, typeof(string), null, CultureInfo.CurrentCulture)))];
        _movementType = MovementTypes[0];
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SelectedProduct)) UpdateSelectedStock(); };
        // Новые строки перемещения — сразу с остатками обоих складов
        Lines.CollectionChanged += (_, e) =>
        {
            foreach (var l in e.NewItems?.OfType<DocumentLineViewModel>() ?? [])
            {
                SetLineStock(l);
                l.Changed += (_, _) => { if (IsWriteOffEditorOpen) RecalcWriteOff(); };
            }
            if (IsWriteOffEditorOpen) RecalcWriteOff();
        };
    }

    private Dictionary<int, (decimal Quantity, decimal AvgCost)> _transferToStock = new();

    /// <summary>Дата проводки перемещения (по умолчанию — сегодня).</summary>
    [ObservableProperty] private DateTime? _transferDate = DateTime.Today;

    private void SetLineStock(DocumentLineViewModel l)
    {
        if (IsWriteOffEditorOpen)
        {
            l.StockFrom = _writeOffStock.TryGetValue(l.Product.Id, out var w) ? w.Quantity : 0;
            return;
        }
        l.StockFrom = StockOf(l.Product.Id);
        l.StockTo = _transferToStock.TryGetValue(l.Product.Id, out var s) ? s.Quantity : 0;
    }

    private void UpdateLinesStock()
    {
        foreach (var l in Lines) SetLineStock(l);
    }

    partial void OnTransferToChanged(Warehouse? value) => _ = RunAsync(LoadTransferStockAsync);

    public record MovementTypeOption(StockMovementType? Type, string Name);

    public override string Title => L.T("Склад");

    public ObservableCollection<Warehouse> Warehouses { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();
    public ObservableCollection<string> ProductNames { get; } = new();
    private List<StockRow> _allBalances = [];
    public ObservableCollection<StockRow> Balances { get; } = new();
    public ICollectionView BalancesView { get; }
    public ObservableCollection<StockTransfer> Transfers { get; } = new();
    public ICollectionView TransfersView { get; }
    public ObservableCollection<StockTransferItem> TransferItems { get; } = new();
    public ObservableCollection<InventoryCount> Inventories { get; } = new();
    public ICollectionView InventoriesView { get; }
    public ObservableCollection<InventoryCountItem> InventoryItems { get; } = new();
    public ObservableCollection<InventoryLineViewModel> InventoryLines { get; } = new();
    public ICollectionView InventoryView { get; }
    public ObservableCollection<StockWriteOff> WriteOffs { get; } = new();
    public ICollectionView WriteOffsView { get; }
    public ObservableCollection<StockWriteOffItem> WriteOffItems { get; } = new();
    public ObservableCollection<StockMovement> Movements { get; } = new();
    public ICollectionView MovementsView { get; }

    /// <summary>Постраничный вывод (одинаковая панель страниц на всех таблицах).</summary>
    public ReportPager<StockRow> BalancesPager { get; } = new(_ => true);
    public ReportPager<InventoryLineViewModel> InventoryPager { get; } = new(_ => true);
    public ReportPager<StockMovement> MovementsPager { get; } = new(_ => true);

    private void SyncInventoryPager(bool resetPage = true)
        => InventoryPager.SetSource(InventoryView.Cast<InventoryLineViewModel>().ToList(), resetPage);
    public IReadOnlyList<MovementTypeOption> MovementTypes { get; }

    [ObservableProperty] private int _selectedTab;

    // ---------- Остатки ----------
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveBalanceFilterCount))] private Warehouse? _filterWarehouse;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveBalanceFilterCount))] private string? _filterCategory;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveBalanceFilterCount))] private string? _filterProduct;
    [ObservableProperty] private string? _search;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveBalanceFilterCount))] private bool _onlyLow;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveBalanceFilterCount))] private bool _showZero;

    /// <summary>Сколько условий фильтра остатков включено — значок на кнопке «Фильтр».</summary>
    public int ActiveBalanceFilterCount =>
        (FilterWarehouse != null ? 1 : 0) + (FilterCategory != null ? 1 : 0) + (FilterProduct != null ? 1 : 0) + (OnlyLow ? 1 : 0) + (ShowZero ? 1 : 0);
    [ObservableProperty] private StockRow? _selectedBalance;
    [ObservableProperty] private int _positionsCount;
    [ObservableProperty] private int _lowCount;
    [ObservableProperty] private decimal _totalQuantity;
    [ObservableProperty] private decimal _stockValue;
    [ObservableProperty] private decimal _stockSaleValue;

    // ---------- Перемещение ----------
    [ObservableProperty] private Warehouse? _transferFrom;
    [ObservableProperty] private Warehouse? _transferTo;
    [ObservableProperty] private string? _transferComment;
    [ObservableProperty] private decimal _selectedProductStock;
    [ObservableProperty] private StockTransfer? _selectedTransfer;

    /// <summary>Режим вкладки: реестр перемещений (false) или окно создания на всю вкладку (true).</summary>
    [ObservableProperty] private bool _isTransferEditorOpen;

    /// <summary>Просмотр состава выбранного перемещения на всю вкладку (по двойному клику).</summary>
    [ObservableProperty] private bool _isTransferDetailOpen;

    [RelayCommand]
    private void OpenTransferDetail()
    {
        if (SelectedTransfer is not null) IsTransferDetailOpen = true;
    }

    [RelayCommand]
    private void CloseTransferDetail() => IsTransferDetailOpen = false;
    [ObservableProperty] private DateTime? _transferPeriodFrom = DateTime.Today.AddMonths(-1);
    [ObservableProperty] private DateTime? _transferPeriodTo = DateTime.Today;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveTransferFilterCount))] private string? _transferSearch;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveTransferFilterCount))] private Warehouse? _transferWarehouseFilter;
    [ObservableProperty] private int _transfersCount;

    public int ActiveTransferFilterCount => TransferWarehouseFilter != null ? 1 : 0;

    partial void OnTransferSearchChanged(string? value) => RefreshTransfers();
    partial void OnTransferWarehouseFilterChanged(Warehouse? value) => RefreshTransfers();

    private void RefreshTransfers()
    {
        TransfersView.Refresh();
        TransfersCount = TransfersView.Cast<object>().Count();
    }

    private async Task LoadTransfersCore()
    {
        Fill(Transfers, await _warehouse.GetTransfersAsync(TransferPeriodFrom, TransferPeriodTo));
        RefreshTransfers();
    }

    [RelayCommand]
    private Task LoadTransfersAsync() => RunAsync(LoadTransfersCore);

    [RelayCommand]
    private Task SetTransferPeriodAsync(string? period)
    {
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        (TransferPeriodFrom, TransferPeriodTo) = period switch
        {
            "today" => ((DateTime?)today, (DateTime?)today),
            "yesterday" => (today.AddDays(-1), today.AddDays(-1)),
            "week" => (today.AddDays(-6), today),
            "month" => (monthStart, today),
            "prevmonth" => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            "quarter" => (new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1), today),
            "year" => (new DateTime(today.Year, 1, 1), today),
            "all" => (null, null),
            _ => (TransferPeriodFrom, TransferPeriodTo)
        };
        return RunAsync(LoadTransfersCore);
    }

    /// <summary>«Сбросить» в панели фильтра — только склад (поиск остаётся).</summary>
    [RelayCommand]
    private void ResetTransferFilters() => TransferWarehouseFilter = null;

    [RelayCommand]
    private void CreateTransfer()
    {
        if (IsWriteOffEditorOpen && Lines.Count > 0) { Dialogs.Error(L.T("Сначала проведите или закройте незавершённое списание.")); return; }
        IsWriteOffEditorOpen = false;
        TransferFrom ??= Warehouses.FirstOrDefault();
        TransferTo ??= Warehouses.Skip(1).FirstOrDefault();
        IsTransferEditorOpen = true;
    }

    [RelayCommand]
    private void CloseTransferEditor()
    {
        if (Lines.Count > 0 && !Dialogs.Confirm(L.T("Закрыть без сохранения? Добавленные позиции будут удалены."))) return;
        ClearLines();
        TransferComment = null;
        TransferDate = DateTime.Today;
        IsTransferEditorOpen = false;
    }

    // ---------- Инвентаризация ----------
    [ObservableProperty] private Warehouse? _inventoryWarehouse;
    [ObservableProperty] private string? _inventoryComment;
    [ObservableProperty] private string? _inventorySearch;
    [ObservableProperty] private bool _inventoryOnlyDifferences;
    [ObservableProperty] private int _inventoryDiffCount;
    [ObservableProperty] private decimal _inventorySurplus;
    [ObservableProperty] private decimal _inventoryShortage;
    [ObservableProperty] private InventoryCount? _selectedInventory;

    /// <summary>Режим вкладки: реестр инвентаризаций (false) или лист создания на всю вкладку (true).</summary>
    [ObservableProperty] private bool _isInventoryEditorOpen;

    /// <summary>Просмотр расхождений выбранной инвентаризации (по двойному клику).</summary>
    [ObservableProperty] private bool _isInventoryDetailOpen;

    [ObservableProperty] private DateTime? _inventoryPeriodFrom = DateTime.Today.AddMonths(-1);
    [ObservableProperty] private DateTime? _inventoryPeriodTo = DateTime.Today;
    [ObservableProperty] private string? _inventoriesSearch;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveInventoryFilterCount))] private Warehouse? _inventoryWarehouseFilter;
    [ObservableProperty] private int _inventoriesCount;

    public int ActiveInventoryFilterCount => InventoryWarehouseFilter != null ? 1 : 0;

    partial void OnInventoriesSearchChanged(string? value) => RefreshInventories();
    partial void OnInventoryWarehouseFilterChanged(Warehouse? value) => RefreshInventories();

    private void RefreshInventories()
    {
        InventoriesView.Refresh();
        InventoriesCount = InventoriesView.Cast<object>().Count();
    }

    private async Task LoadInventoriesCore()
    {
        Fill(Inventories, await _warehouse.GetInventoriesAsync(InventoryPeriodFrom, InventoryPeriodTo));
        RefreshInventories();
    }

    [RelayCommand]
    private Task LoadInventoriesAsync() => RunAsync(LoadInventoriesCore);

    [RelayCommand]
    private Task SetInventoryPeriodAsync(string? period)
    {
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        (InventoryPeriodFrom, InventoryPeriodTo) = period switch
        {
            "today" => ((DateTime?)today, (DateTime?)today),
            "yesterday" => (today.AddDays(-1), today.AddDays(-1)),
            "week" => (today.AddDays(-6), today),
            "month" => (monthStart, today),
            "prevmonth" => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            "quarter" => (new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1), today),
            "year" => (new DateTime(today.Year, 1, 1), today),
            "all" => (null, null),
            _ => (InventoryPeriodFrom, InventoryPeriodTo)
        };
        return RunAsync(LoadInventoriesCore);
    }

    [RelayCommand]
    private void ResetInventoryFilters() => InventoryWarehouseFilter = null;

    [RelayCommand]
    private void CreateInventory()
    {
        InventoryWarehouse ??= Warehouses.FirstOrDefault();
        IsInventoryEditorOpen = true;
    }

    [RelayCommand]
    private void CloseInventoryEditor()
    {
        if (InventoryLines.Any(l => l.HasDifference) && !Dialogs.Confirm(L.T("Закрыть без сохранения? Введённые данные будут удалены."))) return;
        ClearInventoryLines();
        InventoryComment = null;
        IsInventoryEditorOpen = false;
    }

    [RelayCommand]
    private void OpenInventoryDetail()
    {
        if (SelectedInventory is not null) IsInventoryDetailOpen = true;
    }

    [RelayCommand]
    private void CloseInventoryDetail() => IsInventoryDetailOpen = false;

    // ---------- Списание ----------
    private Dictionary<int, (decimal Quantity, decimal AvgCost)> _writeOffStock = new();
    [ObservableProperty] private Warehouse? _writeOffWarehouse;
    [ObservableProperty] private string? _writeOffReason;
    [ObservableProperty] private string? _writeOffComment;
    [ObservableProperty] private DateTime? _writeOffDate = DateTime.Today;
    [ObservableProperty] private decimal _writeOffTotal;
    [ObservableProperty] private StockWriteOff? _selectedWriteOff;
    [ObservableProperty] private bool _isWriteOffEditorOpen;
    [ObservableProperty] private bool _isWriteOffDetailOpen;
    [ObservableProperty] private DateTime? _writeOffPeriodFrom = DateTime.Today.AddMonths(-1);
    [ObservableProperty] private DateTime? _writeOffPeriodTo = DateTime.Today;
    [ObservableProperty] private string? _writeOffsSearch;
    [ObservableProperty] private Warehouse? _writeOffWarehouseFilter;
    [ObservableProperty] private int _writeOffsCount;
    [ObservableProperty] private decimal _writeOffsSum;

    /// <summary>Типовые причины списания (можно ввести свою).</summary>
    public IReadOnlyList<string> WriteOffReasons { get; } =
        [L.T("Порча"), L.T("Брак"), L.T("Истёк срок годности"), L.T("Недостача / кража"), L.T("Собственные нужды"), L.T("Прочее")];

    partial void OnWriteOffsSearchChanged(string? value) => RefreshWriteOffs();
    partial void OnWriteOffWarehouseFilterChanged(Warehouse? value) => RefreshWriteOffs();
    partial void OnWriteOffWarehouseChanged(Warehouse? value) => _ = RunAsync(LoadWriteOffStockAsync);
    partial void OnSelectedWriteOffChanged(StockWriteOff? value) => _ = RunAsync(async () =>
        Fill(WriteOffItems, value is null ? [] : await _warehouse.GetWriteOffItemsAsync(value.Id)));

    private void RefreshWriteOffs()
    {
        WriteOffsView.Refresh();
        var visible = WriteOffsView.Cast<StockWriteOff>().ToList();
        WriteOffsCount = visible.Count;
        WriteOffsSum = visible.Sum(w => w.CostTotal);
    }

    private async Task LoadWriteOffsCore()
    {
        Fill(WriteOffs, await _warehouse.GetWriteOffsAsync(WriteOffPeriodFrom, WriteOffPeriodTo));
        RefreshWriteOffs();
    }

    private async Task LoadWriteOffStockAsync()
    {
        _writeOffStock = WriteOffWarehouse is null ? new() : await _warehouse.GetWarehouseStockAsync(WriteOffWarehouse.Id);
        if (!IsWriteOffEditorOpen) return;
        foreach (var l in Lines)
        {
            SetLineStock(l);
            l.Price = DefaultPrice(l.Product);
        }
        RecalcWriteOff();
    }

    private void RecalcWriteOff() => WriteOffTotal = Lines.Sum(l => l.Amount);

    [RelayCommand]
    private Task LoadWriteOffsAsync() => RunAsync(LoadWriteOffsCore);

    [RelayCommand]
    private Task SetWriteOffPeriodAsync(string? period)
    {
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        (WriteOffPeriodFrom, WriteOffPeriodTo) = period switch
        {
            "today" => ((DateTime?)today, (DateTime?)today),
            "yesterday" => (today.AddDays(-1), today.AddDays(-1)),
            "week" => (today.AddDays(-6), today),
            "month" => (monthStart, today),
            "prevmonth" => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            "quarter" => (new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1), today),
            "year" => (new DateTime(today.Year, 1, 1), today),
            "all" => (null, null),
            _ => (WriteOffPeriodFrom, WriteOffPeriodTo)
        };
        return RunAsync(LoadWriteOffsCore);
    }

    [RelayCommand]
    private Task CreateWriteOffAsync()
    {
        if (IsTransferEditorOpen && Lines.Count > 0) { Dialogs.Error(L.T("Сначала проведите или закройте незавершённое перемещение.")); return Task.CompletedTask; }
        IsTransferEditorOpen = false;
        WriteOffWarehouse ??= Warehouses.FirstOrDefault();
        IsWriteOffEditorOpen = true;
        return RunAsync(LoadWriteOffStockAsync);
    }

    [RelayCommand]
    private void CloseWriteOffEditor()
    {
        if (Lines.Count > 0 && !Dialogs.Confirm(L.T("Закрыть без сохранения? Добавленные позиции будут удалены."))) return;
        ResetWriteOffEditor();
    }

    private void ResetWriteOffEditor()
    {
        ClearLines();
        WriteOffReason = null;
        WriteOffComment = null;
        WriteOffDate = DateTime.Today;
        WriteOffTotal = 0;
        IsWriteOffEditorOpen = false;
    }

    [RelayCommand]
    private void OpenWriteOffDetail()
    {
        if (SelectedWriteOff is not null) IsWriteOffDetailOpen = true;
    }

    [RelayCommand]
    private void CloseWriteOffDetail() => IsWriteOffDetailOpen = false;

    [RelayCommand]
    private async Task PostWriteOffAsync()
    {
        if (WriteOffWarehouse is null) { Dialogs.Error(L.T("Выберите склад.")); return; }
        if (Lines.Count == 0) { Dialogs.Error(L.T("Добавьте товары.")); return; }
        if (string.IsNullOrWhiteSpace(WriteOffReason)) { Dialogs.Error(L.T("Укажите причину списания.")); return; }

        var shortages = Lines.GroupBy(l => l.Product)
            .Select(g => (Product: g.Key, Qty: g.Sum(x => x.Quantity), Stock: _writeOffStock.TryGetValue(g.Key.Id, out var s) ? s.Quantity : 0))
            .Where(x => x.Qty > x.Stock)
            .Select(x => $"• {x.Product.Name}: {x.Qty:0.###} > {x.Stock:0.###} {x.Product.Unit}")
            .ToList();
        if (shortages.Count > 0)
        {
            Dialogs.Error(L.F("Недостаточно товара на складе «{0}»:\n{1}", WriteOffWarehouse.Name, string.Join("\n", shortages)));
            return;
        }
        RecalcWriteOff();
        if (!Dialogs.Confirm(L.F("Списать {0} поз. со склада «{1}» на сумму {2:N2}?", Lines.Count, WriteOffWarehouse.Name, WriteOffTotal))) return;

        var date = (WriteOffDate ?? DateTime.Today).Date + DateTime.Now.TimeOfDay;
        var ok = await RunAsync(() => _warehouse.WriteOffAsync(WriteOffWarehouse.Id,
            Lines.Select(l => new TransferLine(l.Product.Id, l.Quantity)).ToList(), WriteOffReason, WriteOffComment, date));
        if (!ok) return;
        Dialogs.Info(L.T("Списание проведено."));
        ResetWriteOffEditor();
        await LoadAsync();
    }

    [RelayCommand]
    private void ExportWriteOffs()
    {
        var lines = WriteOffsView.Cast<StockWriteOff>()
            .Select(w => Row(w.Number, w.Date.ToString("dd.MM.yyyy HH:mm"), w.Warehouse?.Name, w.Reason, w.Items.Count, w.CostTotal, w.Comment))
            .Prepend(L.T("Номер;Дата;Склад;Причина;Позиций;Сумма;Комментарий"));
        SaveExcel($"{L.T("Списания")}_{DateTime.Today:yyyyMMdd}.xlsx", lines);
    }

    // ---------- Движение ----------
    [ObservableProperty] private DateTime _from = DateTime.Today.AddDays(-7);
    [ObservableProperty] private DateTime _to = DateTime.Today;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveMovementFilterCount))] private Warehouse? _movementWarehouse;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveMovementFilterCount))] private MovementTypeOption _movementType;
    [ObservableProperty] private string? _movementSearch;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveMovementFilterCount))] private Product? _movementProduct;

    /// <summary>Сколько условий фильтра движения включено — значок на кнопке «Фильтр».</summary>
    public int ActiveMovementFilterCount =>
        (MovementWarehouse != null ? 1 : 0) + (MovementType?.Type != null ? 1 : 0) + (MovementProduct != null ? 1 : 0);

    partial void OnMovementWarehouseChanged(Warehouse? value) => _ = RunAsync(LoadMovementsCore);
    partial void OnMovementTypeChanged(MovementTypeOption value) => _ = RunAsync(LoadMovementsCore);
    partial void OnMovementProductChanged(Product? value) => _ = RunAsync(LoadMovementsCore);
    [ObservableProperty] private decimal _movementsIn;
    [ObservableProperty] private decimal _movementsOut;

    /// <summary>В списании цена строки — себестоимость на складе (для суммы); в перемещении не нужна.</summary>
    protected override decimal DefaultPrice(Product product) =>
        !IsWriteOffEditorOpen ? 0
        : _writeOffStock.TryGetValue(product.Id, out var s) && s.AvgCost != 0 ? s.AvgCost : product.PurchasePrice;

    private static bool Matches(string? search, params string?[] fields) =>
        string.IsNullOrWhiteSpace(search) || fields.Any(f => f?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true);

    private bool MatchBalance(StockRow r) =>
        (!OnlyLow || r.IsLow || r.IsNegative)
        && (FilterCategory is null || r.Category == FilterCategory)
        && (FilterProduct is null || r.Product == FilterProduct)
        && Matches(Search, r.Product, r.Sku, r.Category);

    partial void OnSearchChanged(string? value) => RefreshBalances();
    partial void OnOnlyLowChanged(bool value) => RefreshBalances();
    partial void OnFilterCategoryChanged(string? value) { FillProductNames(); RefreshBalances(); }
    partial void OnFilterProductChanged(string? value) => RefreshBalances();

    /// <summary>Список товаров фильтра — в пределах выбранной категории.</summary>
    private void FillProductNames()
    {
        var current = FilterProduct;
        Fill(ProductNames, _allBalances.Where(r => FilterCategory is null || r.Category == FilterCategory)
            .Select(r => r.Product).Distinct().Order(StringComparer.CurrentCultureIgnoreCase));
        FilterProduct = current is not null && ProductNames.Contains(current) ? current : null;
    }
    partial void OnShowZeroChanged(bool value) => _ = RunAsync(LoadBalancesCore);
    partial void OnFilterWarehouseChanged(Warehouse? value) => _ = RunAsync(LoadBalancesCore);

    partial void OnInventorySearchChanged(string? value) { InventoryView.Refresh(); SyncInventoryPager(); }
    partial void OnInventoryOnlyDifferencesChanged(bool value) { InventoryView.Refresh(); SyncInventoryPager(); }
    partial void OnMovementSearchChanged(string? value) { MovementsView.Refresh(); RecalcMovements(); }

    partial void OnTransferFromChanged(Warehouse? value) => _ = RunAsync(LoadTransferStockAsync);

    partial void OnSelectedTransferChanged(StockTransfer? value) => _ = RunAsync(async () =>
        Fill(TransferItems, value is null ? [] : await _warehouse.GetTransferItemsAsync(value.Id)));

    partial void OnSelectedInventoryChanged(InventoryCount? value) => _ = RunAsync(async () =>
        Fill(InventoryItems, value is null ? [] : await _warehouse.GetInventoryItemsAsync(value.Id)));

    partial void OnInventoryWarehouseChanged(Warehouse? value)
    {
        if (InventoryLines.Count > 0) ClearInventoryLines();
    }

    public override Task LoadAsync() => RunAsync(async () =>
    {
        SetProducts(await _catalog.GetAllAsync<Product>(activeOnly: true));
        Fill(Warehouses, await _catalog.GetAllAsync<Warehouse>(true));
        TransferFrom ??= Warehouses.FirstOrDefault();
        TransferTo ??= Warehouses.Skip(1).FirstOrDefault();
        InventoryWarehouse ??= Warehouses.FirstOrDefault();
        WriteOffWarehouse ??= Warehouses.FirstOrDefault();
        await LoadBalancesCore();
        await LoadTransferStockAsync();
        await LoadTransfersCore();
        await LoadInventoriesCore();
        await LoadWriteOffsCore();
        await LoadWriteOffStockAsync();
        await LoadMovementsCore();
    });

    // ================= Остатки =================

    private async Task LoadBalancesCore()
    {
        var rows = await _warehouse.GetBalancesAsync(FilterWarehouse?.Id, ShowZero);
        Fill(Balances, rows);
        var current = FilterCategory;
        Fill(Categories, rows.Select(r => r.Category).OfType<string>().Distinct().Order(StringComparer.CurrentCultureIgnoreCase));
        _allBalances = rows.ToList();
        FilterCategory = current is not null && Categories.Contains(current) ? current : null;
        FillProductNames();
        RefreshBalances();
    }

    private void RefreshBalances()
    {
        BalancesView.Refresh();
        var visible = BalancesView.Cast<StockRow>().ToList();
        BalancesPager.SetSource(visible);
        PositionsCount = visible.Count;
        LowCount = visible.Count(r => r.IsLow || r.IsNegative);
        TotalQuantity = visible.Sum(r => r.Quantity);
        StockValue = visible.Sum(r => r.CostValue);
        StockSaleValue = visible.Sum(r => r.SaleValue);
    }

    [RelayCommand]
    private void ShowAllWarehouses() => FilterWarehouse = null;

    [RelayCommand]
    private void ResetBalanceFilters()
    {
        Search = null;
        FilterCategory = null;
        FilterProduct = null;
        OnlyLow = false;
        ShowZero = false;
        FilterWarehouse = null;
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    /// <summary>Открывает журнал движения по выбранному товару.</summary>
    [RelayCommand]
    private Task ShowProductMovementsAsync(StockRow? row)
    {
        row ??= SelectedBalance;
        if (row is null) return Task.CompletedTask;
        MovementSearch = null;
        From = DateTime.Today.AddMonths(-3);
        To = DateTime.Today;
        SelectedTab = 4;
        var product = Products.FirstOrDefault(p => p.Id == row.ProductId);
        if (product is not null && MovementProduct != product)
        {
            MovementProduct = product; // перезагрузка — в OnMovementProductChanged
            return Task.CompletedTask;
        }
        return RunAsync(LoadMovementsCore);
    }

    [RelayCommand]
    private void ExportBalances()
    {
        var lines = BalancesView.Cast<StockRow>()
            .Select(r => Row(r.Product, r.Sku, r.Category, r.Warehouse, r.Quantity, r.Unit, r.AvgCost, r.CostValue, r.SalePrice, r.SaleValue))
            .Prepend(L.T("Товар;Артикул;Категория;Склад;Остаток;Ед.;Ср. себест.;Сумма по себест.;Цена;Сумма по цене"));
        SaveExcel($"{L.T("Остатки")}_{DateTime.Today:yyyyMMdd}.xlsx", lines);
    }

    // ================= Перемещение =================

    private async Task LoadTransferStockAsync()
    {
        _transferStock = TransferFrom is null ? new() : await _warehouse.GetWarehouseStockAsync(TransferFrom.Id);
        _transferToStock = TransferTo is null ? new() : await _warehouse.GetWarehouseStockAsync(TransferTo.Id);
        UpdateSelectedStock();
        UpdateLinesStock();
    }

    private void UpdateSelectedStock() =>
        SelectedProductStock = SelectedProduct is not null && _transferStock.TryGetValue(SelectedProduct.Id, out var s) ? s.Quantity : 0;

    private decimal StockOf(int productId) => _transferStock.TryGetValue(productId, out var s) ? s.Quantity : 0;

    [RelayCommand]
    private void SwapWarehouses() => (TransferFrom, TransferTo) = (TransferTo, TransferFrom);

    [RelayCommand]
    private async Task PostTransferAsync()
    {
        if (TransferFrom is null || TransferTo is null) { Dialogs.Error(L.T("Выберите склады.")); return; }
        if (TransferFrom.Id == TransferTo.Id) { Dialogs.Error(L.T("Склады отправителя и получателя должны различаться.")); return; }
        if (Lines.Count == 0) { Dialogs.Error(L.T("Добавьте товары.")); return; }

        // Проверка остатков до отправки в сервис — понятное сообщение по всем позициям сразу
        var shortages = Lines.GroupBy(l => l.Product)
            .Select(g => (Product: g.Key, Qty: g.Sum(x => x.Quantity), Stock: StockOf(g.Key.Id)))
            .Where(x => x.Qty > x.Stock)
            .Select(x => $"• {x.Product.Name}: {x.Qty:0.###} > {x.Stock:0.###} {x.Product.Unit}")
            .ToList();
        if (shortages.Count > 0)
        {
            Dialogs.Error(L.F("Недостаточно товара на складе «{0}»:\n{1}", TransferFrom.Name, string.Join("\n", shortages)));
            return;
        }
        if (!Dialogs.Confirm(L.F("Переместить {0} поз. со склада «{1}» на склад «{2}»?", Lines.Count, TransferFrom.Name, TransferTo.Name))) return;

        // Выбранный день + текущее время (порядок документов внутри дня сохраняется)
        var date = (TransferDate ?? DateTime.Today).Date + DateTime.Now.TimeOfDay;
        var ok = await RunAsync(() => _warehouse.TransferAsync(TransferFrom.Id, TransferTo.Id,
            Lines.Select(l => new TransferLine(l.Product.Id, l.Quantity)).ToList(), TransferComment, date));
        if (!ok) return;
        Dialogs.Info(L.T("Перемещение проведено."));
        ClearLines();
        TransferComment = null;
        TransferDate = DateTime.Today;
        IsTransferEditorOpen = false;
        await LoadAsync();
    }

    // ================= Инвентаризация =================

    /// <summary>Заполняет лист всеми активными товарами; факт = учёт (правятся только расхождения).</summary>
    [RelayCommand]
    private Task FillInventoryAsync() => RunAsync(async () =>
    {
        if (InventoryWarehouse is null) { Dialogs.Error(L.T("Выберите склад.")); return; }
        if (InventoryLines.Any(l => l.HasDifference)
            && !Dialogs.Confirm(L.T("Введённые данные будут заменены. Продолжить?"))) return;
        var stock = await _warehouse.GetWarehouseStockAsync(InventoryWarehouse.Id);
        ClearInventoryLines();
        foreach (var p in Products.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var (qty, cost) = stock.TryGetValue(p.Id, out var s) ? s : (0m, 0m);
            var line = new InventoryLineViewModel(p, qty, cost != 0 ? cost : p.PurchasePrice);
            line.Changed += OnInventoryLineChanged;
            InventoryLines.Add(line);
        }
        SyncInventoryPager();
        RecalcInventory();
    });

    /// <summary>Добавляет в лист выбранный товар (факт = введённое количество); если уже есть — прибавляет.</summary>
    [RelayCommand]
    private Task AddInventoryLineAsync()
    {
        if (SelectedProduct is null) { Dialogs.Error(L.T("Выберите товар.")); return Task.CompletedTask; }
        if (NewQuantity < 0) { Dialogs.Error(L.T("Фактический остаток не может быть отрицательным.")); return Task.CompletedTask; }
        var product = SelectedProduct;
        var qty = NewQuantity;
        return RunAsync(async () =>
        {
            await AddInventoryProductAsync(product, qty);
            SelectedProduct = null;
            NewQuantity = 1;
            Barcode = "";
        });
    }

    /// <summary>Скан штрихкода/артикула: +1 к факту; неизвестный код — предложение создать товар.</summary>
    [RelayCommand]
    private async Task AddInventoryByBarcodeAsync()
    {
        var code = Barcode?.Trim();
        if (string.IsNullOrEmpty(code)) return;
        var product = Products.FirstOrDefault(p => string.Equals(p.Barcode, code, StringComparison.OrdinalIgnoreCase))
                      ?? Products.FirstOrDefault(p => string.Equals(p.Sku, code, StringComparison.OrdinalIgnoreCase));
        if (product is null)
        {
            if (!Dialogs.Confirm(L.F("Товар со штрихкодом/артикулом «{0}» не найден.\nСоздать новый товар?", code))) return;
            var created = await CatalogDialogs.EditAsync(Products, new Product { Barcode = code });
            if (created?.Saved is not { } p) return;
            product = p;
        }
        await RunAsync(() => AddInventoryProductAsync(product, 1));
        Barcode = "";
    }

    private async Task AddInventoryProductAsync(Product product, decimal qty)
    {
        if (InventoryWarehouse is null) { Dialogs.Error(L.T("Выберите склад.")); return; }
        var existing = InventoryLines.FirstOrDefault(l => l.Product.Id == product.Id);
        if (existing is not null)
        {
            existing.Actual += qty;
            return;
        }
        var stock = await _warehouse.GetWarehouseStockAsync(InventoryWarehouse.Id);
        var (book, cost) = stock.TryGetValue(product.Id, out var s) ? s : (0m, 0m);
        var line = new InventoryLineViewModel(product, book, cost != 0 ? cost : product.PurchasePrice) { Actual = qty };
        line.Changed += OnInventoryLineChanged;
        InventoryLines.Insert(0, line);
        InventoryView.Refresh();
        SyncInventoryPager();
        RecalcInventory();
    }

    /// <summary>Обнуляет факт по всем видимым строкам — удобно для полного пересчёта.</summary>
    [RelayCommand]
    private void ZeroVisibleActual()
    {
        foreach (var l in InventoryView.Cast<InventoryLineViewModel>().ToList()) l.Actual = 0;
    }

    [RelayCommand]
    private void ResetInventoryActual()
    {
        foreach (var l in InventoryLines) l.Actual = l.Book < 0 ? 0 : l.Book;
    }

    [RelayCommand]
    private void ClearInventory()
    {
        if (InventoryLines.Any(l => l.HasDifference) && !Dialogs.Confirm(L.T("Очистить лист инвентаризации?"))) return;
        ClearInventoryLines();
    }

    private void ClearInventoryLines()
    {
        foreach (var l in InventoryLines) l.Changed -= OnInventoryLineChanged;
        InventoryLines.Clear();
        SyncInventoryPager();
        RecalcInventory();
    }

    private void OnInventoryLineChanged(object? sender, EventArgs e)
    {
        RecalcInventory();
        if (InventoryOnlyDifferences) { InventoryView.Refresh(); SyncInventoryPager(resetPage: false); }
    }

    private void RecalcInventory()
    {
        InventoryDiffCount = InventoryLines.Count(l => l.HasDifference);
        InventorySurplus = InventoryLines.Where(l => l.Difference > 0).Sum(l => l.DifferenceAmount);
        InventoryShortage = InventoryLines.Where(l => l.Difference < 0).Sum(l => l.DifferenceAmount);
    }

    [RelayCommand]
    private async Task PostInventoryAsync()
    {
        if (InventoryWarehouse is null || InventoryLines.Count == 0) { Dialogs.Error(L.T("Сначала заполните лист инвентаризации.")); return; }
        if (InventoryLines.Any(l => l.Actual < 0)) { Dialogs.Error(L.T("Фактический остаток не может быть отрицательным.")); return; }
        if (InventoryDiffCount == 0 && !Dialogs.Confirm(L.T("Расхождений нет. Всё равно провести инвентаризацию?"))) return;
        if (InventoryDiffCount > 0 && !Dialogs.Confirm(L.F("Провести инвентаризацию?\nПозиций с расхождениями: {0}\nИзлишки: {1:N2}\nНедостача: {2:N2}",
                InventoryDiffCount, InventorySurplus, InventoryShortage))) return;

        var ok = await RunAsync(() => _warehouse.CreateInventoryAsync(InventoryWarehouse.Id,
            InventoryLines.Select(l => new InventoryLine(l.Product.Id, l.Actual)).ToList(), InventoryComment));
        if (!ok) return;
        ClearInventoryLines();
        InventoryComment = null;
        IsInventoryEditorOpen = false;
        Dialogs.Info(L.T("Инвентаризация проведена, остатки скорректированы."));
        await LoadAsync();
    }

    // ================= Движение =================

    private async Task LoadMovementsCore()
    {
        if (From > To) (From, To) = (To, From);
        Fill(Movements, await _warehouse.GetMovementsAsync(From, To, MovementProduct?.Id, MovementWarehouse?.Id, MovementType.Type));
        MovementsView.Refresh();
        RecalcMovements();
    }

    private void RecalcMovements()
    {
        var visible = MovementsView.Cast<StockMovement>().ToList();
        MovementsPager.SetSource(visible);
        MovementsIn = visible.Where(m => m.Quantity > 0).Sum(m => m.Quantity);
        MovementsOut = -visible.Where(m => m.Quantity < 0).Sum(m => m.Quantity);
    }

    [RelayCommand]
    private Task LoadMovementsAsync() => RunAsync(LoadMovementsCore);

    [RelayCommand]
    private Task ClearMovementProductAsync()
    {
        MovementProduct = null;
        return RunAsync(LoadMovementsCore);
    }

    [RelayCommand]
    private Task ClearMovementWarehouseAsync()
    {
        MovementWarehouse = null;
        return RunAsync(LoadMovementsCore);
    }

    [RelayCommand]
    private Task ResetMovementFiltersAsync()
    {
        MovementSearch = null;
        MovementProduct = null;
        MovementWarehouse = null;
        MovementType = MovementTypes[0];
        return RunAsync(LoadMovementsCore);
    }

    [RelayCommand]
    private Task SetMovementPeriodAsync(string? period)
    {
        var today = DateTime.Today;
        (From, To) = period switch
        {
            "today" => (today, today),
            "week" => (today.AddDays(-6), today),
            "month" => (new DateTime(today.Year, today.Month, 1), today),
            _ => (From, To)
        };
        return RunAsync(LoadMovementsCore);
    }

    [RelayCommand]
    private void ExportMovements()
    {
        var conv = new EnumNameConverter();
        var lines = MovementsView.Cast<StockMovement>()
            .Select(m => Row(m.Date.ToString("dd.MM.yyyy HH:mm"), m.DocumentNumber,
                conv.Convert(m.Type, typeof(string), null, CultureInfo.CurrentCulture), m.Product?.Name, m.Warehouse?.Name, m.Quantity, m.UnitCost))
            .Prepend(L.T("Дата;Документ;Операция;Товар;Склад;Количество;Себест."));
        SaveExcel($"{L.T("Движение")}_{From:yyyyMMdd}-{To:yyyyMMdd}.xlsx", lines);
    }

    // ================= Общее =================

    private void SaveExcel(string name, IEnumerable<string> lines)
    {
        var path = Dialogs.SaveFile(name, ExcelExport.Filter);
        if (path is null) return;
        try
        {
            ExcelExport.Save(path, lines, Path.GetFileNameWithoutExtension(name));
            Dialogs.Info(L.F("Файл сохранён: {0}", path));
        }
        catch (IOException ex) { Dialogs.Error(ex.Message); }
    }

    private static string Row(params object?[] values) => string.Join(';', values.Select(v => v switch
    {
        decimal d => d.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU")),
        null => "",
        _ => '"' + v.ToString()!.Replace("\"", "\"\"") + '"'
    }));
}
