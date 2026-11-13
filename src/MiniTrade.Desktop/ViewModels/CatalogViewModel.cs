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
        var result = await _catalog.GetPageAsync<T>(Search, ShowArchived, (Page - 1) * PageSize, PageSize);
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
        catch (Exception ex) { _dialogs.Error(L.F("Непредвиденная ошибка: {0}", ex.Message)); }
    }

    private bool CanPrev() => Page > 1;
    private bool CanNext() => Page < PageCount;

    [RelayCommand(CanExecute = nameof(CanPrev))] private Task FirstPageAsync() { Page = 1; return SafeLoadAsync(); }
    [RelayCommand(CanExecute = nameof(CanPrev))] private Task PrevPageAsync() { Page--; return SafeLoadAsync(); }
    [RelayCommand(CanExecute = nameof(CanNext))] private Task NextPageAsync() { Page++; return SafeLoadAsync(); }
    [RelayCommand(CanExecute = nameof(CanNext))] private Task LastPageAsync() { Page = PageCount; return SafeLoadAsync(); }

    [RelayCommand]
    private Task NewAsync() => OpenAsync(null);

    [RelayCommand]
    private Task EditAsync(T? item) => item is null ? Task.CompletedTask : OpenAsync(item);

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

    public CatalogViewModel(CatalogService catalog, CatalogTransferService transfer, SettingsService settings, IDialogService dialogs, ICatalogDialogs catalogDialogs) : base(dialogs)
    {
        _settings = settings;
        _transfer = transfer;
        Products = new(L.T("Товары"), catalog, dialogs, catalogDialogs);
        Categories = new(L.T("Категории"), catalog, dialogs, catalogDialogs);
        Counterparties = new(L.T("Контрагенты"), catalog, dialogs, catalogDialogs);
        CounterpartyGroups = new(L.T("Группы контрагентов"), catalog, dialogs, catalogDialogs);
        Warehouses = new(L.T("Склады"), catalog, dialogs, catalogDialogs);
        CashRegisters = new(L.T("Кассы"), catalog, dialogs, catalogDialogs);
        Articles = new(L.T("Статьи"), catalog, dialogs, catalogDialogs);
        // Название категории показывается в таблице товаров
        Categories.Changed += async (_, _) => await Products.LoadAsync();
        CounterpartyGroups.Changed += async (_, _) => await Counterparties.LoadAsync();
    }

    public override string Title => L.T("Справочники");

    public CatalogEditor<Product> Products { get; }
    public CatalogEditor<Category> Categories { get; }
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
        await RunAsync(async () => SaleReceiptPrinter.PrintLabels([(product, 1)], await _settings.GetCompanyAsync()));
    }

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
        await Counterparties.LoadAsync();
        await CounterpartyGroups.LoadAsync();
        await Warehouses.LoadAsync();
        await CashRegisters.LoadAsync();
        await Articles.LoadAsync();
    });
}
