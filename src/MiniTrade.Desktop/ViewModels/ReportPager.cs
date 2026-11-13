using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>
/// Постраничный вывод строк отчёта (в памяти): отчёт загружается целиком, а таблица показывает
/// только текущую страницу — так даже 100 000 строк не тормозят интерфейс.
/// Набор кнопок и подписей совпадает с журналами (Продажи, Финансы, Справочники).
/// </summary>
public sealed partial class ReportPager<T> : ObservableObject
{
    private readonly Func<T, bool> _filter;
    private IReadOnlyList<T> _source = [];
    private List<T> _filtered = [];

    public ReportPager(Func<T, bool> filter) => _filter = filter;

    /// <summary>Строки текущей страницы.</summary>
    public ObservableCollection<T> Items { get; } = new();

    /// <summary>Все строки после поиска/фильтра (для итогов и экспорта).</summary>
    public IReadOnlyList<T> Filtered => _filtered;

    public IReadOnlyList<int> PageSizes { get; } = [25, 50, 100, 200, 500, 1000, 10000, 100000];

    [ObservableProperty] private int _pageSize = 100;
    private int _page = 1;

    public int Page
    {
        get => _page;
        set { if (_page != value) { _page = value; ShowPage(); } }
    }

    public int TotalCount => _filtered.Count;
    public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    public int FirstNumber => (Page - 1) * PageSize;

    public string PageInfo => TotalCount == 0
        ? L.T("Ничего не найдено")
        : L.F("Показано {0}–{1} из {2:N0}", FirstNumber + 1, FirstNumber + Items.Count, TotalCount);

    public string PageLabel => L.F("{0} из {1}", Page, PageCount);

    public void SetSource(IReadOnlyList<T> rows, bool resetPage = true)
    {
        _source = rows;
        Refresh(resetPage);
    }

    /// <summary>Повторно применяет фильтр (после изменения поиска).</summary>
    public void Refresh(bool resetPage = true)
    {
        _filtered = _source.Where(_filter).ToList();
        if (resetPage) _page = 1;
        ShowPage();
    }

    partial void OnPageSizeChanged(int value) { _page = 1; ShowPage(); }

    private void ShowPage()
    {
        if (_page > PageCount) _page = PageCount;
        if (_page < 1) _page = 1;
        Items.Clear();
        foreach (var r in _filtered.Skip((_page - 1) * PageSize).Take(PageSize)) Items.Add(r);
        OnPropertyChanged(nameof(Page));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(FirstNumber));
        OnPropertyChanged(nameof(PageInfo));
        OnPropertyChanged(nameof(PageLabel));
        FirstPageCommand.NotifyCanExecuteChanged();
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
    }

    private bool CanGoBack() => Page > 1;
    private bool CanGoForward() => Page < PageCount;

    [RelayCommand(CanExecute = nameof(CanGoBack))] private void FirstPage() => Page = 1;
    [RelayCommand(CanExecute = nameof(CanGoBack))] private void PrevPage() => Page--;
    [RelayCommand(CanExecute = nameof(CanGoForward))] private void NextPage() => Page++;
    [RelayCommand(CanExecute = nameof(CanGoForward))] private void LastPage() => Page = PageCount;
}
