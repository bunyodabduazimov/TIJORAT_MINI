using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>
/// Один справочник: постраничная таблица с поиском (выборка и поиск — в базе);
/// добавление и изменение — в модальном окне.
/// </summary>
public partial class CatalogEditor<T> : ObservableObject where T : class, ICatalogEntity, new()
{
    private readonly CatalogService _catalog;
    private readonly IDialogService _dialogs;
    private readonly ICatalogDialogs _catalogDialogs;
    private CancellationTokenSource? _searchDelay;
    private int _loadVersion;

    public CatalogEditor(string title, CatalogService catalog, IDialogService dialogs, ICatalogDialogs catalogDialogs)
    {
        Title = title;
        _catalog = catalog;
        _dialogs = dialogs;
        _catalogDialogs = catalogDialogs;
    }

    public string Title { get; }

    /// <summary>Текст кнопки создания на панели вкладки.</summary>
    public string NewLabel { get; init; } = L.T("+ Добавить");

    // Права вкладки: catalog.<вкладка>.create/edit/delete
    public bool CanCreate { get; } = Core.Services.Access.Can(Perm.Catalog(typeof(T), "create"));
    public bool CanEdit { get; } = Core.Services.Access.Can(Perm.Catalog(typeof(T), "edit"));
    public bool CanDelete { get; } = Core.Services.Access.Can(Perm.Catalog(typeof(T), "delete"));

    /// <summary>Записи текущей страницы.</summary>
    public ObservableCollection<T> Items { get; } = new();

    private static readonly int[] Sizes = [25, 50, 100, 200];
    public IReadOnlyList<int> PageSizes => Sizes;

    [ObservableProperty] private T? _selected;
    [ObservableProperty] private string? _search;
    [ObservableProperty] private bool _showArchived;
    [ObservableProperty] private int _pageSize = 50;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _totalCount;

    public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);

    /// <summary>Сколько записей до текущей страницы — с этого числа продолжается нумерация строк.</summary>
    public int FirstNumber => (Page - 1) * PageSize;

    public string PageInfo => TotalCount == 0
        ? L.T("Ничего не найдено")
        : L.F("Показано {0}–{1} из {2:N0}", FirstNumber + 1, FirstNumber + Items.Count, TotalCount);

    public string PageLabel => L.F("{0} из {1}", Page, PageCount);

    // ── Фильтр (пока только у товаров) ──
    public bool SupportsFilter => typeof(T) == typeof(Product);
    public ProductFilter ProductFilter { get; private set; } = new();
    public bool HasActiveFilter => ProductFilter.ActiveCount > 0;
    public int ActiveFilterCount => ProductFilter.ActiveCount;

    [RelayCommand]
    private async Task OpenFilterAsync()
    {
        if (!SupportsFilter) return;
        var vm = new ProductFilterViewModel(ProductFilter,
            await _catalog.GetAllAsync<Category>(), await _catalog.GetAllAsync<MeasureUnit>());
        ModalHost.ShowDialog(new Views.FilterWindow { DataContext = vm });
        if (vm.Result is not { } applied) return;
        ProductFilter = applied;
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(ActiveFilterCount));
        Page = 1;
        await SafeLoadAsync();
    }

    /// <summary>Вызывается после сохранения/удаления (для обновления зависимых списков).</summary>
    public event EventHandler? Changed;

    // Поиск — с небольшой задержкой, чтобы не обращаться к базе на каждую букву
    partial void OnSearchChanged(string? value)
    {
        _searchDelay?.Cancel();
        var cts = _searchDelay = new CancellationTokenSource();
        _ = ReloadAfterDelayAsync(cts.Token);
    }

    private async Task ReloadAfterDelayAsync(CancellationToken ct)
    {
        try { await Task.Delay(300, ct); }
        catch (TaskCanceledException) { return; }
        Page = 1;
        await SafeLoadAsync();
    }

    partial void OnShowArchivedChanged(bool value) { Page = 1; _ = SafeLoadAsync(); }
    partial void OnPageSizeChanged(int value) { Page = 1; _ = SafeLoadAsync(); }

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        var selectedId = Selected?.Id;
        var result = await _catalog.GetPageAsync<T>(Search, ShowArchived, (Page - 1) * PageSize, PageSize,
            productFilter: SupportsFilter ? ProductFilter : null);
        if (version != _loadVersion) return; // пришёл ответ на устаревший запрос

        // Страница опустела (удалили последние записи) — показываем последнюю непустую
        if (result.Items.Count == 0 && result.TotalCount > 0 && Page > 1)
        {
            Page = Math.Max(1, (result.TotalCount + PageSize - 1) / PageSize);
            await LoadAsync();
            return;
        }

        Items.Clear();
        foreach (var i in result.Items) Items.Add(i);
        TotalCount = result.TotalCount;
        Selected = Items.FirstOrDefault(i => i.Id == selectedId);
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(FirstNumber));
        OnPropertyChanged(nameof(PageInfo));
        OnPropertyChanged(nameof(PageLabel));
        FirstPageCommand.NotifyCanExecuteChanged();
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
    }

    private async Task SafeLoadAsync()
    {
        try { await LoadAsync(); }
        catch (Exception ex) { ErrorLog.Write(ex, GetType().Name); _dialogs.Error(L.F("Непредвиденная ошибка: {0}", ex.Message)); }
    }

    private bool CanPrev() => Page > 1;
    private bool CanNext() => Page < PageCount;

    [RelayCommand(CanExecute = nameof(CanPrev))] private Task FirstPageAsync() { Page = 1; return SafeLoadAsync(); }
    [RelayCommand(CanExecute = nameof(CanPrev))] private Task PrevPageAsync() { Page--; return SafeLoadAsync(); }
    [RelayCommand(CanExecute = nameof(CanNext))] private Task NextPageAsync() { Page++; return SafeLoadAsync(); }
    [RelayCommand(CanExecute = nameof(CanNext))] private Task LastPageAsync() { Page = PageCount; return SafeLoadAsync(); }

    [RelayCommand]
    private Task NewAsync() => CanCreate ? OpenAsync(null) : Task.CompletedTask;

    [RelayCommand]
    private Task EditAsync(T? item) => item is null || !CanEdit ? Task.CompletedTask : OpenAsync(item);

    private async Task OpenAsync(T? item)
    {
        var result = await _catalogDialogs.EditAsync(item);
        if (result is null) return;
        await LoadAsync();
        if (result.Saved is { } saved) Selected = Items.FirstOrDefault(i => i.Id == saved.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Копия записи: открывается форма новой записи, заполненная полями исходной.</summary>
    [RelayCommand]
    private Task CopyAsync(T? item) => item is null ? Task.CompletedTask : OpenAsync(CatalogEntities.CopyOf(item));

    [RelayCommand]
    private async Task DeleteAsync(T? item)
    {
        if (item is null || !_dialogs.Confirm(L.F("Удалить «{0}»?", item.Name))) return;
        try
        {
            await _catalog.DeleteAsync<T>(item.Id);
            await LoadAsync();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Core.Common.BusinessException ex) { _dialogs.Error(L.Message(ex)); }
    }
}

/// <summary>Справочники: товары, категории, контрагенты и их группы, склады, кассы.</summary>
public partial class CatalogViewModel : PageViewModel
{
    private readonly SettingsService _settings;

    private readonly CatalogTransferService _transfer;

    private readonly ScaleSyncService _scaleSync;

    public CatalogViewModel(CatalogService catalog, CatalogTransferService transfer, SettingsService settings, ScaleSyncService scaleSync, IDialogService dialogs, ICatalogDialogs catalogDialogs) : base(dialogs)
    {
        _settings = settings;
        _transfer = transfer;
        _scaleSync = scaleSync;
        Products = new(L.T("Товары"), catalog, dialogs, catalogDialogs) { NewLabel = L.T("+ Новый товар") };
        Categories = new(L.T("Категории"), catalog, dialogs, catalogDialogs) { NewLabel = L.T("+ Новая категория") };
        Units = new(L.T("Единицы"), catalog, dialogs, catalogDialogs) { NewLabel = L.T("+ Новая единица") };
        Scales = new(L.T("Весы"), catalog, dialogs, catalogDialogs) { NewLabel = L.T("+ Новые весы") };
        Counterparties = new(L.T("Контрагенты"), catalog, dialogs, catalogDialogs) { NewLabel = L.T("+ Новый контрагент") };
        CounterpartyGroups = new(L.T("Группы контрагентов"), catalog, dialogs, catalogDialogs) { NewLabel = L.T("+ Новая группа") };
        Warehouses = new(L.T("Склады"), catalog, dialogs, catalogDialogs) { NewLabel = L.T("+ Новый склад") };
        CashRegisters = new(L.T("Кассы"), catalog, dialogs, catalogDialogs) { NewLabel = L.T("+ Новая касса") };
        Articles = new(L.T("Статьи"), catalog, dialogs, catalogDialogs) { NewLabel = L.T("+ Новая статья") };
        // Название категории показывается в таблице товаров
        Categories.Changed += async (_, _) => await Products.LoadAsync();
        Units.Changed += async (_, _) => await Products.LoadAsync(); // переименование единицы меняет товары
        CounterpartyGroups.Changed += async (_, _) => await Counterparties.LoadAsync();
    }

    public override string Title => L.T("Каталоги");

    public CatalogEditor<Product> Products { get; }
    public CatalogEditor<Category> Categories { get; }
    public CatalogEditor<MeasureUnit> Units { get; }
    public CatalogEditor<ScaleDevice> Scales { get; }
    public CatalogEditor<Counterparty> Counterparties { get; }
    public CatalogEditor<CounterpartyGroup> CounterpartyGroups { get; }
    public CatalogEditor<Warehouse> Warehouses { get; }
    public CatalogEditor<CashRegister> CashRegisters { get; }
    public CatalogEditor<FinanceArticle> Articles { get; }

    /// <summary>Этикетка выбранного товара (размер и вид — Настройки → Печать → Этикетка).</summary>
    [RelayCommand]
    private async Task PrintLabelAsync(Product? product)
    {
        product ??= Products.Selected;
        if (product is null)
        {
            Dialogs.Info(L.T("Выберите товар в таблице."));
            return;
        }
        if (!product.IsWeighted)
        {
            await RunAsync(async () => SaleReceiptPrinter.PrintLabels([(product, 1)], await _settings.GetCompanyAsync()));
            return;
        }

        // Весовой товар: спросить вес и напечатать весовой штрихкод «ПП ККККК ВВВВВ К»
        var company = await _settings.GetCompanyAsync();
        if (company.ScalePrefix is not { Length: 2 } prefix)
        {
            Dialogs.Error(L.T("Не задан код весов (Настройки)."));
            return;
        }
        var code = product.Barcode?.Trim() ?? "";
        if (code.Length is < 1 or > 5 || !code.All(char.IsAsciiDigit))
        {
            Dialogs.Error(L.T("У весового товара штрихкод должен быть кодом весов: от 1 до 5 цифр."));
            return;
        }
        var input = Dialogs.Prompt(L.F("Вес товара «{0}», кг (например 1,250):", product.Name), L.T("Этикетка весового товара"));
        if (input is null) return;
        if (!decimal.TryParse(input.Replace('.', ','), System.Globalization.NumberStyles.Number, new System.Globalization.CultureInfo("ru-RU"), out var kg)
            || kg <= 0 || kg > 99.999m)
        {
            Dialogs.Error(L.T("Введите вес от 0,001 до 99,999 кг."));
            return;
        }
        var grams = (int)Math.Round(kg * 1000m);
        var digits = prefix + code.PadLeft(5, '0') + grams.ToString("D5");
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (digits[i] - '0') * (i % 2 == 0 ? 1 : 3);
        var ean = digits + (char)('0' + (10 - sum % 10) % 10);

        var label = new Product
        {
            Id = product.Id, Name = $"{product.Name} · {grams / 1000m:0.000} {product.Unit}", Sku = product.Sku, Unit = product.Unit,
            Barcode = ean, SalePrice = Math.Round(product.SalePrice * grams / 1000m, 2), IsWeighted = true
        };
        await RunAsync(() => { SaleReceiptPrinter.PrintLabels([(label, 1)], company); return Task.CompletedTask; });
    }

    /// <summary>Окно «Обновление весов»: выгрузка изменённых за период весовых товаров в выбранные весы или во все.</summary>
    [RelayCommand]
    private Task UpdateScalesAsync() => RunAsync(async () =>
    {
        var vm = new ScaleSyncViewModel(_scaleSync, Dialogs);
        await vm.InitAsync();
        ModalHost.ShowDialog(new Views.ScaleSyncWindow { DataContext = vm });
    });

    // ================= Импорт / экспорт (CSV, открывается в Excel) =================

    private const string CsvFilter = "CSV (*.csv)|*.csv|Все файлы (*.*)|*.*";

    [RelayCommand] private void ProductsTemplate() => SaveLines(L.T("Шаблон_товары") + ".csv", CatalogTransferService.ProductTemplate());
    [RelayCommand] private void CounterpartiesTemplate() => SaveLines(L.T("Шаблон_контрагенты") + ".csv", CatalogTransferService.CounterpartyTemplate());

    [RelayCommand]
    private Task ExportProductsAsync() => RunAsync(async () =>
        SaveLines($"{L.T("Товары")}_{DateTime.Today:yyyyMMdd}.csv", await _transfer.ExportProductsAsync(Products.ShowArchived)));

    [RelayCommand]
    private Task ExportCounterpartiesAsync() => RunAsync(async () =>
        SaveLines($"{L.T("Контрагенты")}_{DateTime.Today:yyyyMMdd}.csv", await _transfer.ExportCounterpartiesAsync(Counterparties.ShowArchived)));

    [RelayCommand]
    private Task ImportProductsAsync() => ImportAsync(_transfer.ImportProductsAsync, async () =>
    {
        await Categories.LoadAsync();
        await Products.LoadAsync();
    });

    [RelayCommand]
    private Task ImportCounterpartiesAsync() => ImportAsync(_transfer.ImportCounterpartiesAsync, async () =>
    {
        await CounterpartyGroups.LoadAsync();
        await Counterparties.LoadAsync();
    });

    private async Task ImportAsync(Func<string, CancellationToken, Task<ImportResult>> import, Func<Task> reload)
    {
        var path = Dialogs.OpenFile(CsvFilter);
        if (path is null) return;
        await RunAsync(async () =>
        {
            string text;
            try { text = await File.ReadAllTextAsync(path, Encoding.UTF8); }
            catch (IOException ex) { Dialogs.Error(ex.Message); return; }
            // Файл, сохранённый Excel как «CSV (разделители — запятые)», бывает в кодировке Windows-1251
            if (text.Contains('�'))
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = await File.ReadAllTextAsync(path, Encoding.GetEncoding(1251));
            }
            var result = await import(text, CancellationToken.None);
            await reload();
            var msg = L.F("Добавлено: {0}, обновлено: {1}.", result.Added, result.Updated);
            if (result.Errors.Count > 0)
                msg += "\n\n" + L.F("Пропущено строк: {0}", result.Errors.Count) + "\n" + string.Join("\n", result.Errors.Take(15))
                       + (result.Errors.Count > 15 ? "\n…" : "");
            Dialogs.Info(msg);
        });
    }

    private void SaveLines(string name, IEnumerable<string> lines)
    {
        var path = Dialogs.SaveFile(name, "CSV (*.csv)|*.csv");
        if (path is null) return;
        try
        {
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
            Dialogs.Info(L.F("Файл сохранён: {0}", path));
        }
        catch (IOException ex) { Dialogs.Error(ex.Message); }
    }

    public override Task LoadAsync() => RunAsync(async () =>
    {
        await Products.LoadAsync();
        await Categories.LoadAsync();
        await Units.LoadAsync();
        await Scales.LoadAsync();
        await Counterparties.LoadAsync();
        await CounterpartyGroups.LoadAsync();
        await Warehouses.LoadAsync();
        await CashRegisters.LoadAsync();
        await Articles.LoadAsync();
    });
}

/// <summary>Окно фильтра товаров и услуг.</summary>
public partial class ProductFilterViewModel : ObservableObject, ICloseRequest
{
    private readonly IReadOnlyList<MeasureUnit> _units;

    public ProductFilterViewModel(ProductFilter current, IEnumerable<Category> categories, IEnumerable<MeasureUnit> units)
    {
        _units = units.ToList();
        CategoryChoices = [new(L.T("Все категории"), null), new(L.T("Без категории"), ProductFilter.NoCategory),
            .. categories.Select(c => new FilterChoice<int>(c.Name, c.Id))];
        UnitChoices = [new(L.T("Все единицы"), null), .. _units.Select(u => new FilterChoice<int>(u.Name, u.Id))];
        TypeChoices = [new(L.T("Товары и услуги"), null), new(L.T("Только товары"), false), new(L.T("Только услуги"), true)];
        WeightedChoices = [new(L.T("Все"), null), new(L.T("Весовые"), true), new(L.T("Не весовые"), false)];
        ActiveChoices = [new(L.T("Активные"), true), new(L.T("Все"), null), new(L.T("Неактивные"), false)];
        DeletedChoices = [new(L.T("Без удалённых"), false), new(L.T("С удалёнными"), null), new(L.T("Только удалённые"), true)];
        PhotoChoices = [new(L.T("Все"), null), new(L.T("С фото"), true), new(L.T("Без фото"), false)];
        BarcodeChoices = [new(L.T("Все"), null), new(L.T("Со штрихкодом"), true), new(L.T("Без штрихкода"), false)];
        Load(current);
    }

    public string Title => L.T("Фильтр товаров и услуг");
    public double DialogWidth => 460;
    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<FilterChoice<int>> CategoryChoices { get; }
    public IReadOnlyList<FilterChoice<int>> UnitChoices { get; }
    public IReadOnlyList<FilterChoice<bool>> TypeChoices { get; }
    public IReadOnlyList<FilterChoice<bool>> WeightedChoices { get; }
    public IReadOnlyList<FilterChoice<bool>> ActiveChoices { get; }
    public IReadOnlyList<FilterChoice<bool>> DeletedChoices { get; }
    public IReadOnlyList<FilterChoice<bool>> PhotoChoices { get; }
    public IReadOnlyList<FilterChoice<bool>> BarcodeChoices { get; }

    [ObservableProperty] private FilterChoice<int>? _category;
    [ObservableProperty] private FilterChoice<int>? _unit;
    [ObservableProperty] private FilterChoice<bool>? _type;
    [ObservableProperty] private FilterChoice<bool>? _weighted;
    [ObservableProperty] private FilterChoice<bool>? _active;
    [ObservableProperty] private FilterChoice<bool>? _deleted;
    [ObservableProperty] private FilterChoice<bool>? _photo;
    [ObservableProperty] private FilterChoice<bool>? _barcode;
    public ProductFilter? Result { get; private set; }

    private void Load(ProductFilter f)
    {
        Category = CategoryChoices.FirstOrDefault(c => c.Value == f.CategoryId) ?? CategoryChoices[0];
        var unitId = _units.FirstOrDefault(u => u.Name == f.Unit)?.Id;
        Unit = UnitChoices.FirstOrDefault(c => c.Value == unitId) ?? UnitChoices[0];
        Type = TypeChoices.First(c => c.Value == f.IsService);
        Weighted = WeightedChoices.First(c => c.Value == f.IsWeighted);
        Active = ActiveChoices.First(c => c.Value == f.IsActive);
        Deleted = DeletedChoices.First(c => c.Value == f.IsDeleted);
        Photo = PhotoChoices.First(c => c.Value == f.HasPhoto);
        Barcode = BarcodeChoices.First(c => c.Value == f.HasBarcode);
    }

    [RelayCommand]
    private void Reset() => Load(new ProductFilter());

    [RelayCommand]
    private void Apply()
    {
        Result = new ProductFilter(Category?.Value, _units.FirstOrDefault(u => u.Id == Unit?.Value)?.Name,
            Type?.Value, Weighted?.Value, Active?.Value, Photo?.Value, Barcode?.Value, Deleted?.Value);
        CloseRequested?.Invoke(this, true);
    }
}
