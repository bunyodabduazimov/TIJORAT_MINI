using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Окно «Обновление весов»: весовые товары, изменённые за период, выгружаются в выбранные весы (или во все).</summary>
public partial class ScaleSyncViewModel : ObservableObject
{
    private readonly ScaleSyncService _sync;
    private readonly IDialogService _dialogs;

    public ScaleSyncViewModel(ScaleSyncService sync, IDialogService dialogs)
    {
        _sync = sync;
        _dialogs = dialogs;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is Product p && Matches(p);
    }

    public ObservableCollection<ScaleDevice> Scales { get; } = new();
    public ObservableCollection<Product> Items { get; } = new();
    public ICollectionView ItemsView { get; }

    [ObservableProperty] private DateTime? _from = DateTime.Today;
    [ObservableProperty] private DateTime? _to = DateTime.Today;
    /// <summary>null — выгрузить во все активные весы.</summary>
    [ObservableProperty] private ScaleDevice? _selectedScale;
    [ObservableProperty] private string? _search;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    private bool _isBusy;

    public int Count => ItemsView.Cast<object>().Count();

    partial void OnFromChanged(DateTime? value) => _ = LoadItemsAsync();
    partial void OnToChanged(DateTime? value) => _ = LoadItemsAsync();
    partial void OnSearchChanged(string? value) { ItemsView.Refresh(); OnPropertyChanged(nameof(Count)); }

    private bool Matches(Product p)
    {
        var s = Search?.Trim();
        return string.IsNullOrEmpty(s)
            || p.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase)
            || (p.Barcode?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
            || (p.Sku?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public async Task InitAsync()
    {
        foreach (var s in await _sync.GetScalesAsync()) Scales.Add(s);
        await LoadItemsAsync();
    }

    private async Task LoadItemsAsync()
    {
        try
        {
            var list = await _sync.GetChangedProductsAsync(From, To);
            Items.Clear();
            foreach (var p in list) Items.Add(p);
            OnPropertyChanged(nameof(Count));
        }
        catch (Exception ex) { _dialogs.Error(ex.Message); }
    }

    private bool CanSync => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncAsync()
    {
        if (Scales.Count == 0)
        {
            _dialogs.Info(L.T("Сначала добавьте весы на вкладке «Весы» (тип, IP-адрес и порт)."));
            return;
        }
        var products = ItemsView.Cast<Product>().ToList();
        if (products.Count == 0)
        {
            _dialogs.Info(L.T("Нет изменённых весовых товаров за выбранный период."));
            return;
        }
        IsBusy = true;
        try
        {
            var results = await _sync.SyncAsync(SelectedScale?.Id, products);
            var sb = new StringBuilder();
            foreach (var r in results)
            {
                sb.AppendLine(r.Online
                    ? L.F("{0}: выгружено товаров — {1}", r.Scale, r.Sent)
                    : L.F("{0}: весы недоступны", r.Scale));
                foreach (var e in r.Errors.Take(10)) sb.AppendLine("   • " + e);
                if (r.Errors.Count > 10) sb.AppendLine("   …");
            }
            if (results.Count == 0) _dialogs.Info(L.T("Нет активных весов."));
            else if (results.Any(r => !r.Online || r.Errors.Count > 0)) _dialogs.Error(sb.ToString().TrimEnd());
            else _dialogs.Info(L.T("Обновление весов завершено.") + "\n\n" + sb.ToString().TrimEnd());
        }
        catch (Exception ex) { _dialogs.Error(ex.Message); }
        finally { IsBusy = false; }
    }
}
