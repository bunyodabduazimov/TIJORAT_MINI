using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniTrade.Core.Data;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.Licensing;
using MiniTrade.Desktop.ViewModels;
using MiniTrade.Desktop.Views;

namespace MiniTrade.Desktop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Настройки интерфейса (тема, язык) этого пользователя.</summary>
    public static UiSettings Ui { get; private set; } = new();

    /// <summary>
    /// Пересоздаёт главное окно (после смены языка): тексты берутся заново, вход повторно не нужен.
    /// Открывает страницу <typeparamref name="TPage"/>.
    /// </summary>
    public static void ReloadMainWindow<TPage>(Action<TPage>? configure = null) where TPage : PageViewModel =>
        ReloadMainWindow().Navigate<TPage>(configure);

    /// <summary>Новое главное окно на странице «после входа» из настроек интерфейса.</summary>
    public static MainViewModel ReloadMainWindow()
    {
        var app = (App)Current;
        var old = app.MainWindow;
        var vm = ActivatorUtilities.CreateInstance<MainViewModel>(Services);
        var main = new MainWindow { DataContext = vm };
        if (old is not null)
        {
            main.WindowState = old.WindowState;
            if (old.WindowState == WindowState.Normal)
            {
                main.WindowStartupLocation = WindowStartupLocation.Manual;
                (main.Left, main.Top, main.Width, main.Height) = (old.Left, old.Top, old.Width, old.Height);
            }
        }
        app.MainWindow = main; // до закрытия старого — иначе OnMainWindowClose завершит приложение
        main.Show();
        old?.Close();
        (old?.DataContext as IDisposable)?.Dispose();
        return vm;
    }

    /// <summary>Выход из аккаунта: главное окно прячется, показывается вход; после входа — новое главное окно.</summary>
    public static void SignOut()
    {
        var app = (App)Current;
        try { Task.Run(() => Services.GetRequiredService<AuditService>().LogSessionAsync(Core.Domain.AuditAction.Logout)).Wait(); }
        catch (Exception) { /* журнал не должен мешать выходу */ }
        Services.GetRequiredService<UserSession>().SignOut();
        app.MainWindow?.Hide();
        var login = new LoginWindow { DataContext = Services.GetRequiredService<LoginViewModel>() };
        if (login.ShowDialog() == true) ReloadMainWindow();
        else app.Shutdown();
    }

    /// <summary>Не делать автокопию при выходе (после восстановления базы).</summary>
    public static bool SkipAutoBackup { get; set; }
    private static bool _started;

    /// <summary>Перезапуск программы (после восстановления базы).</summary>
    private static Mutex? _instance;

    public static void Restart()
    {
        SkipAutoBackup = true;
        try { _instance?.ReleaseMutex(); } catch (ApplicationException) { }
        if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe);
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Автоматическая резервная копия базы при выходе (только если программа была полностью запущена)
        if (_started && !SkipAutoBackup && !Services.GetRequiredService<DatabaseConnection>().IsRemote)
        {
            try { DatabaseBackup.RunAuto(Services.GetRequiredService<AppConfig>().ResolvedDatabasePath, Ui); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { }
        }

#if DEBUG
        // Подсказка переводчику: какие строки ещё не переведены на текущий язык
        if (L.Missing.Count > 0)
        {
            try
            {
                System.IO.Directory.CreateDirectory(AppConfig.DataDirectory);
                System.IO.File.WriteAllLines(System.IO.Path.Combine(AppConfig.DataDirectory, $"missing-{L.Current.Code}.txt"), L.Missing);
            }
            catch (System.IO.IOException) { }
        }
#endif
        base.OnExit(e);
    }

    public App()
    {
        // Корректный ввод десятичных чисел при UpdateSourceTrigger=PropertyChanged
        FrameworkCompatibilityPreferences.KeepTextBoxDisplaySynchronizedWithTextProperty = false;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        TabAccess.Register(); // скрытая правами вкладка не остаётся выбранной
        // Одна копия программы: вторая (двойной щелчок) могла одновременно создавать базу и падать на UNIQUE.
        // Ожидание — чтобы Restart() успел дождаться выхода прежнего процесса.
        _instance = new Mutex(false, @"Local\TIJORAT.PRO.MiniTrade");
        bool owned;
        try { owned = _instance.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) { Shutdown(); return; }
        Infrastructure.Ui.RegisterNumericFocus();
        // Числа и суммы — в формате ru-RU (сомони); язык интерфейса — отдельно, из настроек
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            if (a.ExceptionObject is Exception ex) ErrorLog.Write(ex, "AppDomain, критическая");
        };
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            ErrorLog.Write(a.Exception, "фоновая задача");
            a.SetObserved();
        };

        // Интерфейс: язык и тема — до создания первого окна
        Ui = UiSettings.Load();
        L.Load(Ui.Language);
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(L.Culture.IetfLanguageTag)));
        ThemeManager.Apply(Ui.Theme);

        var config = AppConfig.Load();
        var connection = DatabaseSettings.Load(config);
        Services = ConfigureServices(config, connection);

        // 1. База (локальная SQLite или сервер): схема создаётся/обновляется автоматически
        try
        {
            var factory = Services.GetRequiredService<IDbContextFactory<TradeDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await DbInitializer.InitializeAsync(db);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, $"открытие базы данных: {connection.Display}");
            var reason = ex.InnerException?.Message ?? ex.Message;
            if (connection.IsRemote)
            {
                // Сервер недоступен — предложить работать с локальной базой этого компьютера
                if (MessageWindow.Show(L.F("Не удалось подключиться к серверу базы данных:\n{0}\n\n{1}\n\nПереключиться на локальную базу этого компьютера?", connection.Display, reason),
                        "TIJORAT LITE", MessageKind.Question))
                {
                    DatabaseSettings.Save(connection with { Provider = DatabaseProvider.Sqlite });
                    Restart();
                    return;
                }
            }
            else
                MessageWindow.Show(L.F("Не удалось открыть базу данных:\n{0}\n\n{1}", connection.Display, reason), "TIJORAT LITE", MessageKind.Error);
            Shutdown(1);
            return;
        }

        // 2. Лицензия: офлайн-проверка токена + быстрая онлайн-проверка при наличии сети
        var license = Services.GetRequiredService<LicenseManager>();
        if (license.State.IsUsable)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var (outcome, message) = await license.CheckOnlineAsync(cts.Token);
            if (outcome == OnlineCheckOutcome.Revoked)
                MessageWindow.Show(message, L.T("Лицензия"), MessageKind.Warning);
        }
        if (!license.State.IsUsable)
        {
            var activation = new ActivationWindow { DataContext = Services.GetRequiredService<ActivationViewModel>() };
            activation.ShowDialog();
            if (!license.State.IsUsable)
            {
                Shutdown();
                return;
            }
        }

        // 3. Вход пользователя
        var login = new LoginWindow { DataContext = Services.GetRequiredService<LoginViewModel>() };
        if (login.ShowDialog() != true)
        {
            Shutdown();
            return;
        }

        // Пустая база: предложить демо-компанию выбранного направления
        await DemoDataWindow.OfferAsync(Services.GetRequiredService<IDbContextFactory<TradeDbContext>>(),
            Services.GetRequiredService<UserSession>());

        // Нагрузочные тестовые данные: MiniTrade.Desktop.exe --seed-demo
        if (e.Args.Contains("--seed-demo", StringComparer.OrdinalIgnoreCase))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await DemoDataGenerator.RunAsync(Services.GetRequiredService<IDbContextFactory<TradeDbContext>>(),
                    Services.GetRequiredService<UserSession>(),
                    progress: new Progress<string>(s => System.Diagnostics.Debug.WriteLine(s)));
                MessageWindow.Show($"Тестовые данные созданы за {sw.Elapsed:mm\\:ss}", "TIJORAT LITE", MessageKind.Info);
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex, "генерация тестовых данных");
                MessageWindow.Show($"Ошибка генерации тестовых данных:\n{ex.InnerException?.Message ?? ex.Message}", "TIJORAT LITE", MessageKind.Error);
            }
        }

        // 4. Главное окно
        var main = new MainWindow { DataContext = Services.GetRequiredService<MainViewModel>() };
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        _started = true;
        main.Show();
    }

    private static IServiceProvider ConfigureServices(AppConfig config, DatabaseConnection connection)
    {
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(connection);
        services.AddSingleton<IDbContextFactory<TradeDbContext>>(new TradeDbContextFactory(connection));

        // Бизнес-сервисы ядра
        services.AddSingleton<UserSession>();
        services.AddTransient<CatalogService>();
        services.AddTransient<ScaleSyncService>();
        services.AddTransient<CatalogTransferService>();
        services.AddTransient<SalesService>();
        services.AddTransient<PurchaseService>();
        services.AddTransient<WarehouseService>();
        services.AddTransient<ProductionService>();
        services.AddTransient<FinanceService>();
        services.AddTransient<StatementService>();
        services.AddTransient<ReportService>();
        services.AddTransient<DashboardService>();
        services.AddTransient<SettingsService>();
        services.AddTransient<AuditService>();

        // Лицензирование
        services.AddSingleton<DeviceIdentity>();
        services.AddHttpClient<LicenseClient>(c =>
        {
            c.BaseAddress = new Uri(config.LicenseServerUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton<LicenseManager>();
        services.AddHttpClient<UpdateService>(c =>
        {
            c.BaseAddress = new Uri(config.LicenseServerUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromMinutes(10); // скачивание установщика
        });

        // UI
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ICatalogDialogs, CatalogDialogs>();
        services.AddTransient<ActivationViewModel>();
        services.AddSingleton<ActivationViewModelFactory>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<FinanceViewModel>();
        services.AddTransient<SalesViewModel>();
        services.AddTransient<PurchasesViewModel>();
        services.AddTransient<WarehouseViewModel>();
        services.AddTransient<ProductionViewModel>();
        services.AddTransient<CatalogViewModel>();
        services.AddTransient<ReportsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<AuditLogViewModel>();
        return services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception, "необработанная ошибка интерфейса");
        MessageWindow.Show(L.F("Непредвиденная ошибка:\n{0}", e.Exception.Message), "TIJORAT LITE", MessageKind.Error);
        e.Handled = true;
    }
}
