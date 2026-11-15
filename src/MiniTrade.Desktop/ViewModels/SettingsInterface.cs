using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>
/// Настройки → Компания, карточки «Интерфейс» и «Удобство»: тема, язык, масштаб, стартовая страница, меню.
/// Хранятся на этом компьютере (ui.json), применяются сразу и доступны всем пользователям —
/// поэтому вкладка видна и без права «Компания» (тогда она называется «Интерфейс» и показывает только эти карточки).
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>Вкладка «Компания» (с карточками интерфейса) — первая.</summary>
    public const int CompanyTab = 0;

    public string CompanyTabHeader => CanEditCompany ? L.T("Компания") : L.T("Интерфейс");

    public AppTheme Theme
    {
        get => App.Ui.Theme;
        set
        {
            if (App.Ui.Theme == value) return;
            App.Ui.Theme = value;
            App.Ui.Save();
            ThemeManager.Apply(value);
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<LanguageInfo> Languages { get; } = L.Available();

    /// <summary>Язык: главное окно пересоздаётся с новыми текстами и снова открывает эту вкладку.</summary>
    public LanguageInfo? Language
    {
        get => Languages.FirstOrDefault(l => l.Code == L.Current.Code);
        set
        {
            if (value is null || value.Code == L.Current.Code) return;
            App.Ui.Language = value.Code;
            App.Ui.Save();
            L.Load(value.Code);
            System.Windows.Application.Current.Dispatcher.BeginInvoke(MainViewModel.ReloadOnInterfaceSettings,
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    public IReadOnlyList<Choice<double>> Scales { get; } =
        [new(0.8, "80%"), new(0.9, "90%"), new(1.0, "100%"), new(1.1, "110%"), new(1.25, "125%"), new(1.5, "150%")];

    public Choice<double>? Scale
    {
        get => Scales.OrderBy(s => Math.Abs(s.Value - App.Ui.Scale)).First();
        set
        {
            if (value is null || Math.Abs(value.Value - App.Ui.Scale) < 0.001) return;
            App.Ui.Scale = value.Value;
            App.Ui.Save();
            UiSettings.RaiseScaleChanged();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<Choice<string>> StartPages { get; } =
    [
        new(nameof(DashboardViewModel), L.T("Обзор")),
        new(nameof(FinanceViewModel), L.T("Финансы")),
        new(nameof(SalesViewModel), L.T("Продажи")),
        new(nameof(PurchasesViewModel), L.T("Закупки")),
        new(nameof(WarehouseViewModel), L.T("Склад")),
        new(nameof(CatalogViewModel), L.T("Каталоги")),
        new(nameof(ReportsViewModel), L.T("Аналитика")),
    ];

    public Choice<string>? StartPage
    {
        get => StartPages.FirstOrDefault(p => p.Value == App.Ui.StartPage) ?? StartPages[0];
        set
        {
            if (value is null || value.Value == App.Ui.StartPage) return;
            App.Ui.StartPage = value.Value;
            App.Ui.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Меню сворачивается и кнопкой в заголовке — главное окно само сохраняет значение в ui.json.</summary>
    public bool MenuCollapsed
    {
        get => App.Ui.MenuCollapsed;
        set
        {
            if (App.Ui.MenuCollapsed == value) return;
            if (System.Windows.Application.Current.MainWindow?.DataContext is MainViewModel main) main.IsMenuCollapsed = value;
            else { App.Ui.MenuCollapsed = value; App.Ui.Save(); }
            OnPropertyChanged();
        }
    }
}
