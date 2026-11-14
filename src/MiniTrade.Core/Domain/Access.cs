namespace MiniTrade.Core.Domain;

/// <summary>Роль пользователя: набор разрешённых действий. Роль-администратор имеет все права.</summary>
public class AccessRole : Entity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Встроенная роль «Администратор»: все права, нельзя удалить или ограничить.</summary>
    public bool IsAdmin { get; set; }
    /// <summary>Разрешения через запятую (ключи из <see cref="Perm"/>).</summary>
    public string Permissions { get; set; } = "";

    /// <summary>Разрешения роли (служебная метка версии «@…» не входит).</summary>
    public HashSet<string> PermissionSet() =>
        Permissions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => !k.StartsWith('@')).ToHashSet();

    /// <summary>
    /// Есть ли право. Доступ к модулю («sales.view», «finance.view»…) отдельно не настраивается:
    /// он есть, если в модуле разрешён хотя бы один пункт.
    /// </summary>
    public bool Has(string key)
    {
        if (IsAdmin) return true;
        var set = PermissionSet();
        if (set.Contains(key)) return true;
        return Perm.ModuleAccess.Contains(key) && set.Any(k => k.StartsWith(key[..(key.IndexOf('.') + 1)], StringComparison.Ordinal));
    }
}

/// <summary>Ключи разрешений «модуль.действие».</summary>
public static class Perm
{
    public const string DashboardView = "dashboard.view";

    public const string SalesView = "sales.view";
    public const string SalesCreate = "sales.create";
    public const string SalesEdit = "sales.edit";
    public const string SalesHistory = "sales.history.view";
    public const string SalesDelete = "sales.delete";
    public const string SalesReturnsView = "sales.returns.view";
    public const string SalesReturn = "sales.return";

    public const string PurchasesView = "purchases.view";
    public const string PurchasesCreate = "purchases.create";
    public const string PurchasesEdit = "purchases.edit";
    public const string PurchasesHistory = "purchases.history.view";
    public const string PurchasesDelete = "purchases.delete";
    public const string PurchasesReturn = "purchases.return";
    public const string PurchasesDebts = "purchases.debts.view";
    public const string PurchasesPay = "purchases.pay";

    public const string WarehouseView = "warehouse.view";
    public const string WarehouseStock = "warehouse.stock";
    public const string WarehouseTransfer = "warehouse.transfer";
    public const string WarehouseInventory = "warehouse.inventory";
    public const string WarehouseWriteOff = "warehouse.writeoff";
    public const string WarehouseMovements = "warehouse.movements";

    public const string ProductionView = "production.view";
    public const string ProductionProduce = "production.produce";
    public const string ProductionCancel = "production.cancel";
    public const string ProductionRecipes = "production.recipes";

    /// <summary>Доступ к модулю «Финансы»: кассы и операции, созданные документами.</summary>
    public const string FinanceView = "finance.view";

    public const string FinanceIncomeView = "finance.income.view";
    public const string FinanceIncomeCreate = "finance.income.create";
    public const string FinanceIncomeEdit = "finance.income.edit";
    public const string FinanceIncomeDelete = "finance.income.delete";

    public const string FinanceExpenseView = "finance.expense.view";
    public const string FinanceExpenseCreate = "finance.expense.create";
    public const string FinanceExpenseEdit = "finance.expense.edit";
    public const string FinanceExpenseDelete = "finance.expense.delete";

    public const string FinancePaymentView = "finance.payment.view";
    public const string FinancePaymentCreate = "finance.payment.create";
    public const string FinancePaymentEdit = "finance.payment.edit";
    public const string FinancePaymentDelete = "finance.payment.delete";

    public const string FinanceAccrualView = "finance.accrual.view";
    public const string FinanceAccrualCreate = "finance.accrual.create";
    public const string FinanceAccrualEdit = "finance.accrual.edit";
    public const string FinanceAccrualDelete = "finance.accrual.delete";

    public const string FinanceTransferView = "finance.transfer.view";
    public const string FinanceTransferCreate = "finance.transfer.create";

    /// <summary>Доступ к модулю «Справочники»; вкладки — catalog.&lt;вкладка&gt;.view/create/edit/delete.</summary>
    public const string CatalogView = "catalog.view";
    public const string CatalogProductsImport = "catalog.products.import";
    public const string CatalogProductsPrices = "catalog.products.prices";
    public const string CatalogCounterpartiesImport = "catalog.counterparties.import";

    public const string ReportsView = "reports.view";
    public const string ReportsSales = "reports.sales";
    public const string ReportsPurchases = "reports.purchases";
    public const string ReportsProfit = "reports.profit";
    public const string ReportsCash = "reports.cash";
    public const string ReportsStock = "reports.stock";

    public const string AuditView = "audit.view";

    public const string SettingsCompany = "settings.company";
    public const string SettingsUsers = "settings.users";
    public const string SettingsRoles = "settings.roles";
    public const string SettingsPrint = "settings.print";
    public const string SettingsLicense = "settings.license";
    public const string SettingsDatabase = "settings.database";
    public const string SettingsFiscal = "settings.fiscal";

    /// <summary>Ключи доступа к модулю — вычисляются: есть хотя бы одно право модуля.</summary>
    public static readonly IReadOnlySet<string> ModuleAccess = new HashSet<string>
        { SalesView, PurchasesView, WarehouseView, ProductionView, FinanceView, CatalogView, ReportsView };

    /// <summary>Вкладки «Справочников»: ключ раздела прав по типу записи.</summary>
    public static string CatalogSection(Type entity) => entity.Name switch
    {
        nameof(Product) => "catalog.products",
        nameof(Category) => "catalog.categories",
        nameof(MeasureUnit) => "catalog.units",
        nameof(ScaleDevice) => "catalog.scales",
        nameof(Counterparty) => "catalog.counterparties",
        nameof(CounterpartyGroup) => "catalog.groups",
        nameof(Warehouse) => "catalog.warehouses",
        nameof(CashRegister) => "catalog.cashregisters",
        nameof(FinanceArticle) => "catalog.articles",
        _ => "catalog.other"
    };

    /// <summary>«catalog.products.create» для типа записи и действия view/create/edit/delete.</summary>
    public static string Catalog(Type entity, string action) => $"{CatalogSection(entity)}.{action}";
}

public sealed record PermAction(string Key, string Name);

/// <summary>Подраздел модуля (например, «Приход» в «Финансах») со своими действиями.</summary>
public sealed record PermGroup(string Key, string Name, IReadOnlyList<PermAction> Actions);

public sealed record PermModule(string Key, string Name, IReadOnlyList<PermAction> Actions, IReadOnlyList<PermGroup>? Groups = null)
{
    public IReadOnlyList<PermGroup> SubGroups => Groups ?? [];

    /// <summary>Все действия модуля, включая подразделы.</summary>
    public IEnumerable<PermAction> AllActions => Actions.Concat(SubGroups.SelectMany(g => g.Actions));
}

/// <summary>Все модули и действия — для экрана настройки ролей и проверок.</summary>
public static class PermissionCatalog
{
    private static PermGroup Crud(string key, string name, params PermAction[] extra) => new(key, name,
    [
        new($"{key}.view", "Просмотр"), new($"{key}.create", "Создание"),
        new($"{key}.edit", "Изменение"), new($"{key}.delete", "Удаление"), .. extra
    ]);

    public static readonly IReadOnlyList<PermModule> Modules =
    [
        new("dashboard", "Обзор", [new(Perm.DashboardView, "Просмотр")]),
        new("sales", "Продажи", [], [
            new("sales.pos", "Продажа", [new(Perm.SalesCreate, "Оформление продажи"), new(Perm.SalesEdit, "Изменение продажи")]),
            new("sales.history", "История продаж", [new(Perm.SalesHistory, "Просмотр"), new(Perm.SalesDelete, "Удаление")]),
            new("sales.returns", "Возвраты", [new(Perm.SalesReturnsView, "Просмотр"), new(Perm.SalesReturn, "Оформление возврата")])]),
        new("purchases", "Закупки", [], [
            new("purchases.editor", "Новая закупка", [new(Perm.PurchasesCreate, "Создание"), new(Perm.PurchasesEdit, "Изменение")]),
            new("purchases.history", "История закупок", [
                new(Perm.PurchasesHistory, "Просмотр"), new(Perm.PurchasesDelete, "Удаление"), new(Perm.PurchasesReturn, "Возврат поставщику")]),
            new("purchases.debts", "Поставщики и долги", [new(Perm.PurchasesDebts, "Просмотр"), new(Perm.PurchasesPay, "Оплата")])]),
        new("warehouse", "Склад", [
            new(Perm.WarehouseStock, "Остатки"), new(Perm.WarehouseTransfer, "Перемещение"),
            new(Perm.WarehouseInventory, "Инвентаризация"), new(Perm.WarehouseWriteOff, "Списание"),
            new(Perm.WarehouseMovements, "Движение товаров")]),
        new("production", "Производство", [
            new(Perm.ProductionProduce, "Выпуск"), new(Perm.ProductionCancel, "Отмена выпуска"),
            new(Perm.ProductionRecipes, "Техкарты")]),
        new("finance", "Финансы", [], [
            new("finance.income", "Приход", [
                new(Perm.FinanceIncomeView, "Просмотр"), new(Perm.FinanceIncomeCreate, "Создание"),
                new(Perm.FinanceIncomeEdit, "Изменение"), new(Perm.FinanceIncomeDelete, "Удаление")]),
            new("finance.expense", "Расход", [
                new(Perm.FinanceExpenseView, "Просмотр"), new(Perm.FinanceExpenseCreate, "Создание"),
                new(Perm.FinanceExpenseEdit, "Изменение"), new(Perm.FinanceExpenseDelete, "Удаление")]),
            new("finance.payment", "Платежи контрагентам", [
                new(Perm.FinancePaymentView, "Просмотр"), new(Perm.FinancePaymentCreate, "Создание"),
                new(Perm.FinancePaymentEdit, "Изменение"), new(Perm.FinancePaymentDelete, "Удаление")]),
            new("finance.accrual", "Начисления", [
                new(Perm.FinanceAccrualView, "Просмотр"), new(Perm.FinanceAccrualCreate, "Создание"),
                new(Perm.FinanceAccrualEdit, "Изменение"), new(Perm.FinanceAccrualDelete, "Удаление")]),
            new("finance.transfer", "Перевод между кассами", [
                new(Perm.FinanceTransferView, "Просмотр"), new(Perm.FinanceTransferCreate, "Создание")])]),
        new("catalog", "Каталоги", [], [
            Crud("catalog.products", "Товары и услуги", new PermAction(Perm.CatalogProductsImport, "Импорт"), new PermAction(Perm.CatalogProductsPrices, "Изменение цен")),
            Crud("catalog.categories", "Категории"),
            Crud("catalog.units", "Единицы"),
            Crud("catalog.scales", "Весы"),
            Crud("catalog.counterparties", "Контрагенты", new PermAction(Perm.CatalogCounterpartiesImport, "Импорт")),
            Crud("catalog.groups", "Группы контрагентов"),
            Crud("catalog.warehouses", "Склады"),
            Crud("catalog.cashregisters", "Кассы"),
            Crud("catalog.articles", "Статьи доходов и расходов")]),
        new("reports", "Аналитика", [
            new(Perm.ReportsSales, "Продажи"), new(Perm.ReportsPurchases, "Закупки"),
            new(Perm.ReportsProfit, "Прибыль"), new(Perm.ReportsCash, "Движение денег"), new(Perm.ReportsStock, "Остатки")]),
        new("audit", "История действий", [new(Perm.AuditView, "Просмотр")]),
        new("settings", "Настройки", [
            new(Perm.SettingsCompany, "Компания"), new(Perm.SettingsUsers, "Пользователи"), new(Perm.SettingsRoles, "Роли"),
            new(Perm.SettingsPrint, "Печать"), new(Perm.SettingsLicense, "Лицензия"), new(Perm.SettingsDatabase, "База данных"),
            new(Perm.SettingsFiscal, "Кассовый аппарат")]),
    ];

    /// <summary>Метка версии формата прав в строке роли: роли без неё переводятся при запуске.</summary>
    public const string FormatMarker = "@v2";

    public static IEnumerable<string> AllKeys => Modules.SelectMany(m => m.AllActions).Select(a => a.Key);

    /// <summary>Название действия для сообщений: «Продажи — Удаление», «Финансы — Приход — Создание».</summary>
    public static string Describe(string key)
    {
        foreach (var m in Modules)
        {
            if (m.Actions.FirstOrDefault(a => a.Key == key) is { } a) return $"{m.Name} — {a.Name}";
            foreach (var g in m.SubGroups)
                if (g.Actions.FirstOrDefault(x => x.Key == key) is { } ga) return $"{m.Name} — {g.Name} — {ga.Name}";
        }
        return key;
    }

    /// <summary>Строка прав для сохранения: известные ключи + метка формата.</summary>
    public static string Pack(IEnumerable<string> keys)
    {
        var known = AllKeys.ToHashSet();
        return string.Join(',', keys.Where(known.Contains).Distinct().Append(FormatMarker));
    }

    /// <summary>
    /// Перевод прав роли из прежнего формата (права на модуль целиком) в права по вкладкам и подразделам:
    /// у кого было действие модуля — получает его во всех новых вкладках. null — роль уже в новом формате.
    /// </summary>
    public static string? Upgrade(string permissions)
    {
        var set = permissions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        if (set.Contains(FormatMarker)) return null;
        var add = new List<string>();
        void Grant(string from, params string[] to) { if (set.Contains(from)) add.AddRange(to); }

        // Финансы (если роль ещё не переведена на подразделы)
        if (!set.Any(k => k.StartsWith("finance.") && k.Count(c => c == '.') == 2))
        {
            Grant(Perm.FinanceView, Perm.FinanceIncomeView, Perm.FinanceExpenseView, Perm.FinancePaymentView, Perm.FinanceAccrualView, Perm.FinanceTransferView);
            Grant("finance.create", Perm.FinanceIncomeCreate, Perm.FinanceExpenseCreate, Perm.FinancePaymentCreate);
            Grant("finance.edit", Perm.FinanceIncomeEdit, Perm.FinanceExpenseEdit, Perm.FinancePaymentEdit, Perm.FinanceAccrualEdit);
            Grant("finance.delete", Perm.FinanceIncomeDelete, Perm.FinanceExpenseDelete, Perm.FinancePaymentDelete, Perm.FinanceAccrualDelete);
            Grant("finance.accrue", Perm.FinanceAccrualCreate);
            Grant("finance.transfer", Perm.FinanceTransferCreate);
        }

        // Справочники: действие модуля → то же действие на каждой вкладке
        var catalogTabs = Modules.First(m => m.Key == "catalog").SubGroups.Select(g => g.Key).ToList();
        foreach (var action in new[] { "view", "create", "edit", "delete" })
            Grant(action == "view" ? Perm.CatalogView : $"catalog.{action}", catalogTabs.Select(t => $"{t}.{action}").ToArray());
        Grant("catalog.import", Perm.CatalogProductsImport, Perm.CatalogCounterpartiesImport);
        Grant("catalog.prices", Perm.CatalogProductsPrices);

        Grant(Perm.SalesView, Perm.SalesHistory, Perm.SalesReturnsView);
        Grant(Perm.PurchasesView, Perm.PurchasesHistory, Perm.PurchasesDebts);
        Grant(Perm.WarehouseView, Perm.WarehouseStock, Perm.WarehouseMovements);
        Grant(Perm.ReportsView, Perm.ReportsSales, Perm.ReportsPurchases, Perm.ReportsProfit, Perm.ReportsCash, Perm.ReportsStock);
        Grant(Perm.SettingsUsers, Perm.SettingsRoles);
        Grant(Perm.SettingsCompany, Perm.SettingsPrint, Perm.SettingsLicense, Perm.SettingsFiscal);

        set.UnionWith(add);
        return Pack(set);
    }

    /// <summary>Все действия перечисленных модулей.</summary>
    public static string ModulesAll(params string[] modules) =>
        Pack(Modules.Where(m => modules.Contains(m.Key)).SelectMany(m => m.AllActions).Select(a => a.Key));

    public static string DefaultManager => ModulesAll("dashboard", "sales", "purchases", "warehouse", "production", "finance", "catalog", "reports");

    public static string DefaultCashier => Pack([Perm.DashboardView, Perm.SalesView, Perm.SalesCreate, Perm.SalesHistory,
        Perm.SalesReturnsView, Perm.SalesReturn]);

    /// <summary>Перевод прежних прав по разделам (флаги) в новые разрешения: раздел → все его действия.</summary>
    public static string FromLegacy(Permission p)
    {
        var map = new (Permission Flag, string Module)[]
        {
            (Permission.Dashboard, "dashboard"), (Permission.Finance, "finance"), (Permission.Sales, "sales"),
            (Permission.Purchases, "purchases"), (Permission.Warehouse, "warehouse"), (Permission.Catalog, "catalog"),
            (Permission.Reports, "reports"), (Permission.AuditLog, "audit"), (Permission.Production, "production")
        };
        return ModulesAll(map.Where(x => (p & x.Flag) == x.Flag).Select(x => x.Module).ToArray());
    }
}
