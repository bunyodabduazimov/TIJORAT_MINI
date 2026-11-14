using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using MiniTrade.Core.Data;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.Views;

/// <summary>Предложение заполнить пустую базу демо-компанией: выбор направления и ход заполнения.</summary>
public partial class DemoDataWindow : Window
{
    private readonly IDbContextFactory<TradeDbContext> _factory;
    private readonly UserSession _session;
    private readonly List<Choice> _choices;
    private bool _running;

    public sealed class Choice(DemoProfile profile)
    {
        public DemoProfile Profile { get; } = profile;
        public string Icon => Profile.Icon;
        public string Title => L.T(Profile.Title);
        public string Description => L.T(Profile.Description);
        public bool IsSelected { get; set; }
    }

    public DemoDataWindow(IDbContextFactory<TradeDbContext> factory, UserSession session)
    {
        InitializeComponent();
        _factory = factory;
        _session = session;
        _choices = DemoIndustries.All.Select(p => new Choice(p)).ToList();
        _choices[0].IsSelected = true;
        Industries.ItemsSource = _choices;
        Loaded += (_, _) => FillButton.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_running) { OnSkip(this, e); e.Handled = true; }
        };
    }

    /// <summary>Показать окно, если база пустая и пользователь — администратор. true — данные созданы.</summary>
    public static async Task<bool> OfferAsync(IDbContextFactory<TradeDbContext> factory, UserSession session)
    {
        if (!session.IsAdmin || App.Ui.DemoDataDeclined) return false;
        try
        {
            if (!await DemoCompanySeeder.IsDatabaseEmptyAsync(factory)) return false;
        }
        catch { return false; }
        return new DemoDataWindow(factory, session).ShowDialog() == true;
    }

    private async void OnFill(object sender, RoutedEventArgs e)
    {
        var choice = _choices.FirstOrDefault(c => c.IsSelected) ?? _choices[0];
        _running = true;
        FillButton.IsEnabled = SkipButton.IsEnabled = CloseButton.IsEnabled = DontAsk.IsEnabled = Industries.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        var progress = new Progress<DemoProgress>(p =>
        {
            Progress.Value = p.Fraction;
            // Шаг: «Каталог товаров» или «Документы: 14.03.2026» — переводится часть до двоеточия
            var parts = p.Step.Split(':', 2);
            StepText.Text = $"{p.Fraction:P0} · {L.T(parts[0])}{(parts.Length > 1 ? ":" + parts[1] : "")}";
        });
        try
        {
            // Генерация — в фоне: окно остаётся отзывчивым
            await Task.Run(() => DemoCompanySeeder.RunAsync(_factory, _session, choice.Profile.Industry, progress));
            MessageWindow.Show(L.F("Демо-данные «{0}» созданы: товары, клиенты, поставщики и год работы компании.", L.T(choice.Profile.Title)),
                "TIJORAT.PRO", MessageKind.Info);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, GetType().Name);
            _running = false;
            MessageWindow.Show(L.F("Не удалось заполнить демо-данные:\n{0}", ex.InnerException?.Message ?? ex.Message), "TIJORAT.PRO", MessageKind.Error);
            DialogResult = false;
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (DontAsk.IsChecked == true)
        {
            App.Ui.DemoDataDeclined = true;
            App.Ui.Save();
        }
        DialogResult = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Прервать заполнение посередине нельзя — данные получились бы неполными
        if (_running && DialogResult is null) e.Cancel = true;
        base.OnClosing(e);
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
