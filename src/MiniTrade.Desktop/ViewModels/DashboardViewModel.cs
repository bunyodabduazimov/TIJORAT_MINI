using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Столбик графика продаж; Height — в пикселях, IsToday подсвечивается.</summary>
public sealed record ChartBar(string Label, decimal Amount, double Height, bool IsToday)
{
    public string AmountText => Amount == 0 ? "" : Amount.ToString("N0", L.Culture);
}

/// <summary>Строка «топ товаров» с долей от лидера (0..1) для полоски.</summary>
public sealed record TopProductBar(int Rank, string Product, string QuantityText, decimal Revenue, double Share);

/// <summary>Строка таблицы с порядковым номером.</summary>
public sealed record Numbered<T>(int No, T Item);

public partial class DashboardViewModel(DashboardService dashboard, IDialogService dialogs) : PageViewModel(dialogs)
{
    private const double ChartHeight = 160;
    public override string Title => L.T("Обзор");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayChangeText), nameof(TodayChangeKind), nameof(HasSalesThisWeek),
        nameof(IsLoss), nameof(UpdatedText), nameof(Greeting), nameof(TodayText))]
    private DashboardSummary? _summary;

    public ObservableCollection<ChartBar> Bars { get; } = new();
    public ObservableCollection<Numbered<StockRow>> LowStock { get; } = new();
    public ObservableCollection<TopProductBar> TopProducts { get; } = new();
    public ObservableCollection<Numbered<Sale>> RecentSales { get; } = new();
    /// <summary>Подписи оси Y сверху вниз (max … 0).</summary>
    public ObservableCollection<string> AxisTicks { get; } = new();

    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => L.T("Доброй ночи"),
        < 12 => L.T("Доброе утро"),
        < 18 => L.T("Добрый день"),
        _ => L.T("Добрый вечер")
    };

    public string TodayText => DateTime.Today.ToString("dddd, d MMMM yyyy", L.Culture);

    public string UpdatedText => Summary is null ? "" : L.F("Обновлено в {0}", DateTime.Now.ToString("HH:mm", L.Culture));

    public bool HasSalesThisWeek => Summary?.WeekRevenue > 0;

    public bool IsLoss => Summary?.MonthProfit < 0;

    /// <summary>«▲ 12,5% к вчера» / «вчера продаж не было».</summary>
    public string TodayChangeText => Summary?.TodayChangePercent switch
    {
        null => L.T("вчера продаж не было"),
        > 0 and var p => L.F("▲ {0}% к вчера", p.ToString("0.#", L.Culture)),
        < 0 and var p => L.F("▼ {0}% к вчера", Math.Abs(p).ToString("0.#", L.Culture)),
        _ => L.T("как вчера")
    };

    /// <summary>Up / Down / Flat — цвет бейджа в разметке.</summary>
    public string TodayChangeKind => Summary?.TodayChangePercent switch
    {
        > 0 => "Up",
        < 0 => "Down",
        _ => "Flat"
    };

    [RelayCommand]
    public override Task LoadAsync() => RunAsync(async () =>
    {
        var s = await dashboard.GetSummaryAsync();

        var max = s.Last7Days.Count == 0 ? 0 : s.Last7Days.Max(d => d.Amount);
        Replace(Bars, s.Last7Days.Select(d => new ChartBar(
            d.Date.ToString("ddd dd.MM", L.Culture), d.Amount,
            max <= 0 || d.Amount <= 0 ? 3 : Math.Max(3, (double)(d.Amount / max) * ChartHeight),
            d.Date == DateTime.Today)));
        var top = max <= 0 ? 4m : max;
        Replace(AxisTicks, Enumerable.Range(0, 5).Select(i => (top * (4 - i) / 4).ToString("N2", L.Culture)));

        var leader = s.TopProducts.Count == 0 ? 0 : s.TopProducts[0].Revenue;
        Replace(TopProducts, s.TopProducts.Select((p, i) => new TopProductBar(i + 1, p.Product,
            $"{p.Quantity.ToString("0.###", L.Culture)} {p.Unit}", p.Revenue,
            leader <= 0 ? 0 : Math.Max(0.04, (double)(p.Revenue / leader)))));

        Replace(LowStock, s.LowStock.Select((r, i) => new Numbered<StockRow>(i + 1, r)));
        Replace(RecentSales, s.RecentSales.Select((r, i) => new Numbered<Sale>(i + 1, r)));
        Summary = s; // последним — карточки обновятся вместе со списками
    });

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    /// <summary>Переход в раздел меню; параметр — тип страницы ({x:Type vm:SalesViewModel}).</summary>
    [RelayCommand]
    private void Open(Type? page)
    {
        if (page is not null && Application.Current.MainWindow?.DataContext is MainViewModel main)
            main.NavigateTo(page);
    }
}
