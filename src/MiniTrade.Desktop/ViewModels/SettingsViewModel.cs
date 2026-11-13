using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.Infrastructure.Printing;
using MiniTrade.Desktop.Licensing;
using MiniTrade.Desktop.Views;
using MiniTrade.Licensing;

namespace MiniTrade.Desktop.ViewModels;

public sealed record RoleOption(UserRole Role, string Name);

/// <summary>Флажок права доступа к разделу.</summary>
public sealed partial class PermissionOption(Permission value, string name) : ObservableObject
{
    public Permission Value { get; } = value;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isChecked;
}

/// <summary>Значение для выпадающего списка с подписью.</summary>
public sealed record Choice<T>(T Value, string Name);

/// <summary>Типовой размер этикетки, мм.</summary>
public sealed record LabelPreset(double WidthMm, double HeightMm)
{
    public override string ToString() => $"{WidthMm:0} × {HeightMm:0}";
}

/// <summary>Настройки: компания, пользователи, печать, база данных, лицензия, интерфейс.</summary>
public partial class SettingsViewModel(
    SettingsService settings, UserSession session, LicenseManager license, AppConfig config, DatabaseConnection connection,
    ActivationViewModelFactory activationFactory, IDialogService dialogs) : PageViewModel(dialogs)
{
    public override string Title => L.T("Настройки");

    public IReadOnlyList<RoleOption> Roles { get; } =
    [
        new(UserRole.Admin, L.T("Администратор")),
        new(UserRole.Manager, L.T("Менеджер")),
        new(UserRole.Cashier, L.T("Кассир"))
    ];

    public bool IsAdmin => session.IsAdmin;

    /// <summary>Номер вкладки «Интерфейс» (последняя) — на неё возвращаемся после смены языка.</summary>
    public const int InterfaceTab = 5;
    [ObservableProperty] private int _selectedTab;

    // Интерфейс: тема применяется сразу, язык — пересозданием главного окна
    [ObservableProperty] private AppTheme _theme = App.Ui.Theme;
    public IReadOnlyList<LanguageInfo> Languages { get; } = L.Available();
    [ObservableProperty] private LanguageInfo? _language = L.Available().FirstOrDefault(l => l.Code == L.Current.Code);

    public IReadOnlyList<Choice<double>> Scales { get; } =
        [new(0.8, "80%"), new(0.9, "90%"), new(1.0, "100%"), new(1.1, "110%"), new(1.25, "125%"), new(1.5, "150%")];
    [ObservableProperty] private Choice<double>? _scale;

    public IReadOnlyList<Choice<string>> StartPages { get; } =
    [
        new(nameof(DashboardViewModel), L.T("Главная")),
        new(nameof(FinanceViewModel), L.T("Финансы")),
        new(nameof(SalesViewModel), L.T("Продажи")),
        new(nameof(PurchasesViewModel), L.T("Закуп")),
        new(nameof(WarehouseViewModel), L.T("Склад")),
        new(nameof(CatalogViewModel), L.T("Справочники")),
        new(nameof(ReportsViewModel), L.T("Отчёты")),
    ];
    [ObservableProperty] private Choice<string>? _startPage;

    [ObservableProperty] private bool _menuCollapsedOnStart = App.Ui.MenuCollapsed;

    partial void OnScaleChanged(Choice<double>? value)
    {
        if (value is null || Math.Abs(value.Value - App.Ui.Scale) < 0.001) return;
        App.Ui.Scale = value.Value;
        App.Ui.Save();
        UiSettings.RaiseScaleChanged();
    }

    partial void OnStartPageChanged(Choice<string>? value)
    {
        if (value is null) return;
        App.Ui.StartPage = value.Value;
        App.Ui.Save();
    }

    partial void OnMenuCollapsedOnStartChanged(bool value)
    {
        App.Ui.MenuCollapsed = value;
        App.Ui.Save();
        if (System.Windows.Application.Current.MainWindow?.DataContext is MainViewModel main)
            main.IsMenuCollapsed = value;
    }

    private void InitInterfaceChoices()
    {
        Scale ??= Scales.OrderBy(s => Math.Abs(s.Value - App.Ui.Scale)).First();
        StartPage ??= StartPages.FirstOrDefault(p => p.Value == App.Ui.StartPage) ?? StartPages[0];
        MenuCollapsedOnStart = App.Ui.MenuCollapsed;
    }

    partial void OnThemeChanged(AppTheme value)
    {
        App.Ui.Theme = value;
        App.Ui.Save();
        ThemeManager.Apply(value);
    }

    partial void OnLanguageChanged(LanguageInfo? value)
    {
        if (value is null || value.Code == L.Current.Code) return;
        App.Ui.Language = value.Code;
        App.Ui.Save();
        L.Load(value.Code);
        // Дать ComboBox закрыться, затем пересоздать окно с новыми текстами
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            () => App.ReloadMainWindow<SettingsViewModel>(s => s.SelectedTab = InterfaceTab),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    // Компания
    [ObservableProperty] private CompanySettings _company = new();

    // Пользователи
    public ObservableCollection<User> Users { get; } = new();
    [ObservableProperty] private User? _selectedUser;
    [ObservableProperty] private User _editingUser = new();
    [ObservableProperty] private string? _newPassword;

    // Лицензия
    [ObservableProperty] private string _licenseType = "";
    [ObservableProperty] private string _licenseKey = "";
    [ObservableProperty] private string _licenseOwner = "";
    [ObservableProperty] private string _licenseTerm = "";
    [ObservableProperty] private string _licenseStatus = "";
    [ObservableProperty] private string _lastCheck = "";
    public string InstallationId => license.Device.InstallationId;
    public string ServerUrl => config.LicenseServerUrl;
    public string DatabasePath => connection.Display;

    // Мой пароль
    public string CurrentUserName => session.CurrentUser is { } u ? $"{u.FullName} ({u.Login})" : "";
    [ObservableProperty] private string? _oldPassword;
    [ObservableProperty] private string? _myNewPassword;
    [ObservableProperty] private string? _myNewPasswordRepeat;

    // Печать (настройки этого компьютера)
    public ObservableCollection<string> Printers { get; } = new();
    /// <summary>Пустая строка — принтер Windows по умолчанию.</summary>
    [ObservableProperty] private string _printerName = App.Ui.PrinterName ?? "";
    [ObservableProperty] private bool _printWithoutDialog = App.Ui.PrintWithoutDialog;

    partial void OnPrinterNameChanged(string value)
    {
        App.Ui.PrinterName = string.IsNullOrEmpty(value) ? null : value;
        App.Ui.Save();
        OnPropertyChanged(nameof(PreviewInfo));
    }

    partial void OnPrintWithoutDialogChanged(bool value)
    {
        App.Ui.PrintWithoutDialog = value;
        App.Ui.Save();
    }

    [RelayCommand]
    private void RefreshPrinters()
    {
        var current = PrinterName;
        var label = LabelPrinterName;
        var receipt = ReceiptPrinterName;
        var invoice = InvoicePrinterName;
        Printers.Clear();
        Printers.Add("");
        var installed = PrintSetup.Printers();
        foreach (var p in installed) Printers.Add(p);
        PrintersFound = installed.Count;
        // сохранённые, но сейчас недоступные принтеры
        foreach (var saved in new[] { current, label, receipt, invoice })
            if (!Printers.Contains(saved)) Printers.Add(saved);
        PrinterName = current;
        LabelPrinterName = label;
        ReceiptPrinterName = receipt;
        InvoicePrinterName = invoice;
        OnPropertyChanged(nameof(PreviewInfo));
    }

    /// <summary>Сколько принтеров найдено в Windows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrintersInfo))]
    private int _printersFound;
    public string PrintersInfo => PrintersFound == 0
        ? L.T("Принтеры в Windows не найдены. Настройки этого компьютера.")
        : L.F("Найдено в Windows: {0}. Настройки этого компьютера.", PrintersFound);

    /// <summary>Пустая строка — основной принтер.</summary>
    [ObservableProperty] private string _receiptPrinterName = App.Ui.Print.ReceiptPrinterName ?? "";
    /// <summary>Пустая строка — основной принтер.</summary>
    [ObservableProperty] private string _invoicePrinterName = App.Ui.Print.InvoicePrinterName ?? "";

    partial void OnReceiptPrinterNameChanged(string value)
    {
        App.Ui.Print.ReceiptPrinterName = string.IsNullOrEmpty(value) ? null : value;
        App.Ui.Save();
        OnPropertyChanged(nameof(PreviewInfo));
    }

    partial void OnInvoicePrinterNameChanged(string value)
    {
        App.Ui.Print.InvoicePrinterName = string.IsNullOrEmpty(value) ? null : value;
        App.Ui.Save();
        OnPropertyChanged(nameof(PreviewInfo));
    }

    private PrinterKind SectionKind => PrintSection switch { 1 => PrinterKind.Invoice, 2 => PrinterKind.Label, _ => PrinterKind.Receipt };

    /// <summary>Строка под заголовком образца: куда и как он будет напечатан.</summary>
    public string PreviewInfo
    {
        get
        {
            var printer = PrintSetup.PrinterFor(SectionKind) is { Length: > 0 } p ? p : L.T("Принтер Windows по умолчанию");
            var paper = PrintSection switch
            {
                1 => Invoice.Landscape ? L.T("A4, альбомная") : L.T("A4, книжная"),
                2 => L.F("{0} × {1} мм", Label.WidthMm, Label.HeightMm),
                _ => ReceiptFormats.FirstOrDefault(f => f.Value == Receipt.Format)?.Name ?? ""
            };
            var copies = PrintSection switch { 1 => Invoice.Copies, 2 => 1, _ => Receipt.Copies };
            return copies > 1 ? $"{printer}  ·  {paper}  ·  {L.F("копий: {0}", copies)}" : $"{printer}  ·  {paper}";
        }
    }

    /// <summary>Вернуть вид текущего документа (чек / накладная / этикетка) к заводским настройкам.</summary>
    [RelayCommand]
    private void ResetPrintSection()
    {
        if (!Dialogs.Confirm(L.T("Вернуть настройки этого документа к значениям по умолчанию?"))) return;
        switch (PrintSection)
        {
            case 1: CopyDefaults(new InvoiceOptions(), Invoice); break;
            case 2: CopyDefaults(new LabelOptions(), Label); break;
            default: CopyDefaults(new ReceiptOptions(), Receipt); break;
        }
    }

    private static void CopyDefaults<T>(T from, T to)
    {
        foreach (var prop in typeof(T).GetProperties().Where(p => p.CanRead && p.CanWrite))
            prop.SetValue(to, prop.GetValue(from));
    }

    // Вид чека, накладной и этикетки — с предпросмотром на образце
    public ReceiptOptions Receipt => App.Ui.Print.Receipt;
    public InvoiceOptions Invoice => App.Ui.Print.Invoice;
    public LabelOptions Label => App.Ui.Print.Label;

    /// <summary>Пустая строка — тот же принтер, что для документов.</summary>
    [ObservableProperty] private string _labelPrinterName = App.Ui.Print.LabelPrinterName ?? "";
    [ObservableProperty] private bool _showPrintPreview = App.Ui.Print.ShowPreview;
    /// <summary>0 — чек, 1 — накладная, 2 — этикетка.</summary>
    [ObservableProperty] private int _printSection;
    [ObservableProperty] private System.Windows.Documents.IDocumentPaginatorSource? _printPreview;

    public IReadOnlyList<Choice<ReceiptFormat>> ReceiptFormats { get; } =
    [
        new(ReceiptFormat.A4, L.T("Лист A4")),
        new(ReceiptFormat.Tape80, L.T("Чековая лента 80 мм")),
        new(ReceiptFormat.Tape58, L.T("Чековая лента 58 мм"))
    ];
    public IReadOnlyList<double> FontSizes { get; } = [7, 8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28];
    public IReadOnlyList<int> CopyOptions { get; } = [1, 2, 3, 4, 5];
    public IReadOnlyList<LabelPreset> LabelPresets { get; } =
        [new(58, 40), new(58, 30), new(43, 25), new(40, 30), new(30, 20), new(100, 50)];

    private readonly PreviewHolder _preview = new();
    private System.Windows.Threading.DispatcherTimer? _previewTimer;
    private bool _printOptionsHooked;

    partial void OnLabelPrinterNameChanged(string value)
    {
        App.Ui.Print.LabelPrinterName = string.IsNullOrEmpty(value) ? null : value;
        App.Ui.Save();
        OnPropertyChanged(nameof(PreviewInfo));
    }

    partial void OnShowPrintPreviewChanged(bool value)
    {
        App.Ui.Print.ShowPreview = value;
        App.Ui.Save();
    }

    partial void OnPrintSectionChanged(int value)
    {
        UpdatePrintPreview();
        OnPropertyChanged(nameof(PreviewInfo));
    }

    /// <summary>Номер вкладки «Печать»: при переходе на неё образец обновляется (реквизиты могли измениться).</summary>
    public const int PrintTab = 2;
    partial void OnSelectedTabChanged(int value)
    {
        if (value == PrintTab && _printOptionsHooked) UpdatePrintPreview();
    }

    private void HookPrintOptions()
    {
        if (_printOptionsHooked) return;
        _printOptionsHooked = true;
        // Слабые подписки: настройки живут всё время работы программы, а страница настроек — нет
        foreach (var o in new System.ComponentModel.INotifyPropertyChanged[] { Receipt, Invoice, Label })
            System.ComponentModel.PropertyChangedEventManager.AddHandler(o, OnPrintOptionChanged, "");
    }

    private void OnPrintOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        App.Ui.Save();
        OnPropertyChanged(nameof(PreviewInfo));
        // Перерисовка с задержкой: при наборе заголовка не строим документ на каждую букву
        _previewTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250),
            System.Windows.Threading.DispatcherPriority.Background, (_, _) => { _previewTimer!.Stop(); UpdatePrintPreview(); },
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    [RelayCommand]
    private void ApplyLabelPreset(LabelPreset? preset)
    {
        if (preset is null) return;
        Label.WidthMm = preset.WidthMm;
        Label.HeightMm = preset.HeightMm;
    }

    private (string Title, System.Windows.Documents.IDocumentPaginatorSource Document, PrinterKind Kind) BuildSample() => PrintSection switch
    {
        1 => (L.T("Образец накладной"), PrintDocuments.Invoice(PrintDocuments.SampleSale(), Company, Invoice), PrinterKind.Invoice),
        2 => (L.T("Образец этикетки"), PrintDocuments.Labels([(PrintDocuments.SampleProduct(), 1)], Company, Label), PrinterKind.Label),
        _ => (L.T("Образец чека"), PrintDocuments.Receipt(PrintDocuments.SampleSale(), Company, Receipt), PrinterKind.Receipt)
    };

    private void UpdatePrintPreview()
    {
        try { PrintPreview = _preview.Show(BuildSample().Document); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException) { PrintPreview = null; }
    }

    [RelayCommand]
    private void PrintSample()
    {
        var (title, document, kind) = BuildSample();
        DocumentPrinter.Send(title, document, 1, kind);
    }

    // Подключение к базе: локальная SQLite (по умолчанию) или общий сервер MySQL
    /// <summary>Программа сейчас работает с сервером (резервное копирование файла недоступно).</summary>
    public bool IsRemoteDatabase => connection.IsRemote;
    public bool IsLocalDatabase => !connection.IsRemote;
    [ObservableProperty] private bool _useRemoteDb = connection.IsRemote;
    public bool UseLocalDb { get => !UseRemoteDb; set => UseRemoteDb = !value; }
    partial void OnUseRemoteDbChanged(bool value) => OnPropertyChanged(nameof(UseLocalDb));
    [ObservableProperty] private string _dbHost = connection.Host;
    [ObservableProperty] private string _dbPort = connection.Port.ToString();
    [ObservableProperty] private string _dbName = connection.Database;
    [ObservableProperty] private string _dbUser = connection.Username;
    [ObservableProperty] private string _dbPassword = connection.Password;
    [ObservableProperty] private bool _isTestingDb;

    private DatabaseConnection? BuildConnection()
    {
        if (!UseRemoteDb) return connection with { Provider = DatabaseProvider.Sqlite };
        if (string.IsNullOrWhiteSpace(DbHost) || string.IsNullOrWhiteSpace(DbName) || string.IsNullOrWhiteSpace(DbUser))
        {
            Dialogs.Error(L.T("Укажите сервер, имя базы данных и пользователя."));
            return null;
        }
        if (!int.TryParse(DbPort, out var port) || port is < 1 or > 65535)
        {
            Dialogs.Error(L.T("Порт — число от 1 до 65535."));
            return null;
        }
        return connection with
        {
            Provider = DatabaseProvider.MySql, Host = DbHost.Trim(), Port = port,
            Database = DbName.Trim(), Username = DbUser.Trim(), Password = DbPassword
        };
    }

    [RelayCommand]
    private async Task TestDatabaseAsync()
    {
        if (BuildConnection() is not { } c) return;
        if (!c.IsRemote)
        {
            Dialogs.Info(L.T("Выбрана локальная база этого компьютера — проверка не требуется."));
            return;
        }
        IsTestingDb = true;
        try
        {
            if (await TradeDatabase.TestAsync(c) is { } error)
                Dialogs.Error(L.F("Не удалось подключиться:\n{0}", error));
            else
                Dialogs.Info(L.T("Подключение к серверу успешно."));
        }
        finally { IsTestingDb = false; }
    }

    [RelayCommand]
    private async Task SaveDatabaseAsync()
    {
        if (!IsAdmin)
        {
            Dialogs.Error(L.T("Менять подключение к базе может только администратор."));
            return;
        }
        if (BuildConnection() is not { } c) return;
        if (c.IsRemote)
        {
            IsTestingDb = true;
            string? error;
            try { error = await TradeDatabase.TestAsync(c); }
            finally { IsTestingDb = false; }
            if (error is not null)
            {
                Dialogs.Error(L.F("Не удалось подключиться:\n{0}", error));
                return;
            }
        }
        var message = c.IsRemote
            ? L.F("Программа будет работать с базой на сервере:\n{0}\n\nЕсли база пустая, в ней будут созданы таблицы и пользователь admin / admin. Данные локальной базы автоматически не переносятся. Программа перезапустится. Продолжить?", c.Display)
            : L.T("Программа будет работать с локальной базой этого компьютера. Программа перезапустится. Продолжить?");
        if (!Dialogs.Confirm(message)) return;
        try
        {
            DatabaseSettings.Save(c);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Dialogs.Error(L.F("Не удалось сохранить настройки: {0}", ex.Message));
            return;
        }
        App.Restart();
    }

    // Резервное копирование
    [ObservableProperty] private bool _autoBackup = App.Ui.AutoBackup;
    [ObservableProperty] private string _backupFolder = DatabaseBackup.FolderOf(App.Ui);
    [ObservableProperty] private int _backupKeep = App.Ui.BackupKeep;
    [ObservableProperty] private string _lastBackup = FormatLastBackup();
    public IReadOnlyList<int> KeepOptions { get; } = [3, 5, 10, 20, 30, 60];

    private static string FormatLastBackup() =>
        App.Ui.LastBackupAt is { } t ? t.ToString("dd.MM.yyyy HH:mm") : L.T("ещё не создавалась");

    partial void OnAutoBackupChanged(bool value)
    {
        App.Ui.AutoBackup = value;
        App.Ui.Save();
    }

    partial void OnBackupKeepChanged(int value)
    {
        App.Ui.BackupKeep = Math.Max(1, value);
        App.Ui.Save();
    }

    partial void OnSelectedUserChanged(User? value)
    {
        EditingUser = value is null ? new User { Role = UserRole.Cashier }
            : new User
            {
                Id = value.Id, Login = value.Login, FullName = value.FullName, Role = value.Role, IsActive = value.IsActive,
                Phone = value.Phone, Note = value.Note, Permissions = value.Permissions
            };
        NewPassword = null;
    }

    partial void OnEditingUserChanged(User value)
    {
        EditingRole = value.Role;
        LoadPermissionOptions(value.EffectivePermissions);
    }

    /// <summary>Роль в форме; при смене — права сбрасываются к правам роли.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditPermissions))]
    private UserRole _editingRole = UserRole.Cashier;

    partial void OnEditingRoleChanged(UserRole value)
    {
        if (EditingUser.Role == value) return;
        EditingUser.Role = value;
        LoadPermissionOptions(RolePermissions.Default(value));
    }

    /// <summary>Администратору доступно всё — флажки прав для него не редактируются.</summary>
    public bool CanEditPermissions => EditingRole != UserRole.Admin;

    public ObservableCollection<PermissionOption> PermissionOptions { get; } =
    [
        new(Permission.Dashboard, L.T("Главная")),
        new(Permission.Finance, L.T("Финансы")),
        new(Permission.Sales, L.T("Продажи")),
        new(Permission.Purchases, L.T("Закуп")),
        new(Permission.Warehouse, L.T("Склад")),
        new(Permission.Catalog, L.T("Справочники")),
        new(Permission.Reports, L.T("Отчёты")),
        new(Permission.AuditLog, L.T("Журнал действий")),
    ];

    private void LoadPermissionOptions(Permission value)
    {
        foreach (var o in PermissionOptions) o.IsChecked = (value & o.Value) == o.Value;
    }

    private Permission CollectPermissions() =>
        PermissionOptions.Where(o => o.IsChecked).Aggregate(Permission.None, (acc, o) => acc | o.Value);

    [RelayCommand]
    private void ResetPermissions() => LoadPermissionOptions(RolePermissions.Default(EditingRole));

    public IReadOnlyList<FilterChoice<CostingMethod>> CostingMethods { get; } =
        [new(L.T("FIFO — первым пришёл, первым ушёл"), CostingMethod.Fifo), new(L.T("Средняя себестоимость (AVG)"), CostingMethod.Average)];

    public override Task LoadAsync() => RunAsync(async () =>
    {
        InitInterfaceChoices();
        Company = await settings.GetCompanyAsync();
        Fill(Users, await settings.GetUsersAsync());
        if (SelectedUser is null) OnSelectedUserChanged(null);
        RefreshLicenseInfo();
        RefreshPrinters();
        HookPrintOptions();
        UpdatePrintPreview();
    });

    private void RefreshLicenseInfo()
    {
        var s = license.Evaluate();
        var p = s.Payload;
        LicenseType = p is null ? "—" : L.T(LicenseDefaults.TypeName(p.Type));
        LicenseKey = p?.LicenseKey ?? "—";
        LicenseOwner = p is null ? "—" : string.IsNullOrEmpty(p.Company) ? p.CustomerName : $"{p.CustomerName}, {p.Company}";
        LicenseTerm = p?.ExpiresAtUtc is { } exp ? L.F("до {0:dd.MM.yyyy} (осталось {1} дн.)", exp.ToLocalTime(), s.DaysLeft) : p is null ? "—" : L.T("Бессрочно");
        LicenseStatus = s.Message;
        LastCheck = license.LastOnlineCheckUtc is { } t ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "—";
    }

    [RelayCommand]
    private async Task SaveCompanyAsync()
    {
        if (await RunAsync(() => settings.SaveCompanyAsync(Company))) Dialogs.Info(L.T("Данные компании сохранены."));
    }

    [RelayCommand]
    private void NewUser() => SelectedUser = null;

    [RelayCommand]
    private async Task SaveUserAsync()
    {
        EditingUser.Role = EditingRole;
        EditingUser.Permissions = CollectPermissions();
        if (!await RunAsync(() => settings.SaveUserAsync(EditingUser, NewPassword))) return;
        // Сменили пароль пользователю, чей пароль запомнен на этом компьютере, — обновляем сохранённый
        if (!string.IsNullOrEmpty(NewPassword) && SavedLogin.Load() is { } saved
            && string.Equals(saved.Login, EditingUser.Login, StringComparison.OrdinalIgnoreCase))
            SavedLogin.Save(saved.Login, NewPassword);
        NewPassword = null;
        Fill(Users, await settings.GetUsersAsync());
        Dialogs.Info(L.T("Пользователь сохранён."));
    }

    /// <summary>Открыть пользователя в форме справа (из меню строки или двойным щелчком).</summary>
    [RelayCommand]
    private void EditUser(User? user)
    {
        if (user is null) return;
        if (SelectedUser == user) OnSelectedUserChanged(user);
        else SelectedUser = user;
    }

    [RelayCommand]
    private async Task DeleteUserAsync(User? user)
    {
        user ??= SelectedUser;
        if (user is null) return;
        if (!Dialogs.Confirm(L.F("Удалить пользователя «{0}» ({1})?", user.FullName, user.Login))) return;
        if (!await RunAsync(() => settings.DeleteUserAsync(user.Id))) return;
        SelectedUser = null;
        Fill(Users, await settings.GetUsersAsync());
    }

    /// <summary>Быстро включить/отключить вход пользователю.</summary>
    [RelayCommand]
    private async Task ToggleUserActiveAsync(User? user)
    {
        if (user is null) return;
        var copy = new User
        {
            Id = user.Id, Login = user.Login, FullName = user.FullName, Role = user.Role, IsActive = !user.IsActive,
            Phone = user.Phone, Note = user.Note, Permissions = user.Permissions
        };
        if (!await RunAsync(() => settings.SaveUserAsync(copy, null))) return;
        Fill(Users, await settings.GetUsersAsync());
        SelectedUser = Users.FirstOrDefault(u => u.Id == copy.Id);
    }

    [RelayCommand]
    private async Task ChangeMyPasswordAsync()
    {
        if (MyNewPassword != MyNewPasswordRepeat)
        {
            Dialogs.Error(L.T("Новый пароль и повтор не совпадают."));
            return;
        }
        if (!await RunAsync(() => settings.ChangeOwnPasswordAsync(OldPassword, MyNewPassword))) return;
        if (SavedLogin.Load() is { } saved && session.CurrentUser is { } me
            && string.Equals(saved.Login, me.Login, StringComparison.OrdinalIgnoreCase))
            SavedLogin.Save(saved.Login, MyNewPassword!);
        OldPassword = MyNewPassword = MyNewPasswordRepeat = null;
        Dialogs.Info(L.T("Пароль изменён."));
    }

    [RelayCommand]
    private void ChooseBackupFolder()
    {
        if (Dialogs.PickFolder(BackupFolder) is not { } folder) return;
        BackupFolder = folder;
        App.Ui.BackupFolder = string.Equals(folder, DatabaseBackup.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? null : folder;
        App.Ui.Save();
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(BackupFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{BackupFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Dialogs.Error(ex.Message);
        }
    }

    private string DumpName() => $"minitrade_{connection.Database}_{DateTime.Now:yyyyMMdd_HHmmss}.sql";

    /// <summary>Ручной SQL-дамп базы на сервере MySQL.</summary>
    private async Task<bool> DumpAsync(string path)
    {
        try
        {
            await Task.Run(() => MySqlDump.ExportAsync(connection, path));
            App.Ui.LastBackupAt = DateTime.Now;
            App.Ui.Save();
            LastBackup = FormatLastBackup();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MySql.Data.MySqlClient.MySqlException or InvalidOperationException)
        {
            Dialogs.Error(L.F("Не удалось создать копию: {0}", ex.Message));
            return false;
        }
    }

    private async Task RestoreRemoteAsync()
    {
        var path = Dialogs.OpenFile(L.T("SQL-дамп (*.sql)|*.sql|Все файлы (*.*)|*.*"), Directory.Exists(BackupFolder) ? BackupFolder : null);
        if (path is null) return;
        try { MySqlDump.Validate(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Dialogs.Error(L.T("Файл не является резервной копией базы TIJORAT MINI."));
            return;
        }
        if (!Dialogs.Confirm(L.F("Данные базы на сервере будут заменены данными из файла:\n{0}\n\nЭто затронет все компьютеры, подключённые к этой базе. Текущая база предварительно сохранится в папку резервных копий. Программа перезапустится. Продолжить?", path)))
            return;
        var safety = Path.Combine(BackupFolder, $"minitrade_before_restore_{DateTime.Now:yyyyMMdd_HHmmss}.sql");
        if (!await DumpAsync(safety)) return;
        try
        {
            await Task.Run(() => MySqlDump.ImportAsync(connection, path));
            Dialogs.Info(L.F("База восстановлена. Прежняя база сохранена:\n{0}\n\nПрограмма будет перезапущена.", safety));
            App.Restart();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MySql.Data.MySqlClient.MySqlException or InvalidOperationException)
        {
            Dialogs.Error(L.F("Не удалось восстановить базу: {0}\n\nПрежняя база сохранена:\n{1}", ex.Message, safety));
        }
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        if (connection.IsRemote)
        {
            var dump = Path.Combine(BackupFolder, DumpName());
            if (await DumpAsync(dump)) Dialogs.Info(L.F("Резервная копия создана:\n{0}", dump));
            return;
        }
        try
        {
            var path = Path.Combine(BackupFolder, $"minitrade_backup_{DateTime.Now:yyyyMMdd_HHmmss}.db");
            DatabaseBackup.Copy(config.ResolvedDatabasePath, path);
            App.Ui.LastBackupAt = DateTime.Now;
            App.Ui.Save();
            LastBackup = FormatLastBackup();
            Dialogs.Info(L.F("Резервная копия создана:\n{0}", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            Dialogs.Error(L.F("Не удалось создать копию: {0}", ex.Message));
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (!IsAdmin)
        {
            Dialogs.Error(L.T("Восстанавливать базу может только администратор."));
            return;
        }
        if (connection.IsRemote)
        {
            await RestoreRemoteAsync();
            return;
        }
        var path = Dialogs.OpenFile(L.T("База SQLite (*.db)|*.db|Все файлы (*.*)|*.*"), Directory.Exists(BackupFolder) ? BackupFolder : null);
        if (path is null) return;
        if (!Dialogs.Confirm(L.F("Текущие данные будут заменены данными из файла:\n{0}\n\nТекущая база предварительно сохранится в папку резервных копий. Программа перезапустится. Продолжить?", path)))
            return;
        try
        {
            var safety = DatabaseBackup.Restore(path, config.ResolvedDatabasePath, App.Ui);
            Dialogs.Info(L.F("База восстановлена. Прежняя база сохранена:\n{0}\n\nПрограмма будет перезапущена.", safety));
            App.Restart();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            Dialogs.Error(L.F("Не удалось восстановить базу: {0}", ex.Message));
        }
    }

    [RelayCommand]
    private async Task CheckLicenseAsync()
    {
        var (outcome, message) = await license.CheckOnlineAsync();
        RefreshLicenseInfo();
        if (outcome == OnlineCheckOutcome.Revoked)
            Dialogs.Error(message + L.T("\nПри следующем запуске потребуется активация."));
        else
            Dialogs.Info(message);
    }

    [RelayCommand]
    private void EnterNewKey()
    {
        var vm = activationFactory.Create();
        vm.IsKeyMode = true;
        new ActivationWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
        RefreshLicenseInfo();
    }

    /// <summary>Резервная копия базы средствами SQLite Online Backup API (безопасно при открытой базе).</summary>
    [RelayCommand]
    private async Task BackupAsync()
    {
        if (connection.IsRemote)
        {
            var dump = Dialogs.SaveFile(DumpName(), L.T("SQL-дамп (*.sql)|*.sql"));
            if (dump is not null && await DumpAsync(dump)) Dialogs.Info(L.F("Резервная копия создана:\n{0}", dump));
            return;
        }
        var path = Dialogs.SaveFile($"minitrade_backup_{DateTime.Now:yyyyMMdd_HHmm}.db", L.T("База SQLite (*.db)|*.db"));
        if (path is null) return;
        try
        {
            DatabaseBackup.Copy(config.ResolvedDatabasePath, path);
            Dialogs.Info(L.F("Резервная копия создана:\n{0}", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            Dialogs.Error(L.F("Не удалось создать копию: {0}", ex.Message));
        }
    }
}

/// <summary>Отображение пустого имени принтера как «принтер по умолчанию».</summary>
public sealed class PrinterNameConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type t, object? p, System.Globalization.CultureInfo c) =>
        value is string { Length: > 0 } s ? s
        : p is "own" ? L.T("Как основной принтер")
        : L.T("Принтер Windows по умолчанию");
    public object ConvertBack(object? value, Type t, object? p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
}
