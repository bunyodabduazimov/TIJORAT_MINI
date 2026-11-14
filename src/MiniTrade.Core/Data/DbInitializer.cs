using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Data;

/// <summary>Создаёт/обновляет схему локальной базы и заполняет начальные данные.</summary>
public static class DbInitializer
{
    public const string DefaultAdminLogin = "admin";
    public const string DefaultAdminPassword = "admin";

    /// <summary>Стартовый набор единиц измерения: краткое и полное наименование.</summary>
    public static readonly (string Name, string? Full)[] DefaultUnits =
        [("шт", "Штука"), ("кг", "Килограмм")];

    /// <summary>Стартовый набор групп контрагентов.</summary>
    public static readonly string[] DefaultCounterpartyGroups = ["Покупатели", "Поставщики", "Сотрудники"];

    public static async Task InitializeAsync(TradeDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        try { await SeedAsync(db, ct); }
        catch (DbUpdateException)
        {
            // Начальные данные уже записал другой процесс одновременно с нами — перечитываем и досоздаём недостающее
            db.ChangeTracker.Clear();
            await SeedAsync(db, ct);
        }
    }

    public static async Task SeedAsync(TradeDbContext db, CancellationToken ct = default)
    {
        if (!await db.CompanySettings.AnyAsync(ct))
            db.CompanySettings.Add(new CompanySettings());
        else if (await db.CompanySettings.FirstOrDefaultAsync(c => c.Country == null, ct) is { } company)
            // База до появления страны: страна по валюте учёта (TJS → Таджикистан)
            company.Country = Countries.All.FirstOrDefault(c => c.Currency == company.Currency)?.Code ?? Countries.DefaultCode;

        await SeedRolesAsync(db, ct);

        if (!await db.Users.AnyAsync(ct))
            db.Users.Add(new User
            {
                Login = DefaultAdminLogin,
                FullName = "Администратор",
                Role = UserRole.Admin,
                AccessRole = db.AccessRoles.Local.First(r => r.IsAdmin),
                PasswordHash = PasswordHasher.Hash(DefaultAdminPassword)
            });

        if (!await db.Warehouses.AnyAsync(ct))
            db.Warehouses.Add(new Warehouse { Name = "Основной склад" });

        if (!await db.CashRegisters.AnyAsync(ct))
            db.CashRegisters.Add(new CashRegister { Name = "Основная касса" });

        // Единицы измерения: стартовый набор + те, что уже встречаются в товарах
        if (!await db.MeasureUnits.AnyAsync(ct))
        {
            var used = await db.Products.Select(p => p.Unit).Distinct().ToListAsync(ct);
            var units = DefaultUnits.ToList();
            foreach (var u in used.Where(u => !string.IsNullOrWhiteSpace(u)))
                if (!units.Any(x => string.Equals(x.Name, u.Trim(), StringComparison.CurrentCultureIgnoreCase)))
                    units.Add((u.Trim(), null));
            foreach (var (name, full) in units) db.MeasureUnits.Add(new MeasureUnit { Name = name, Description = full });
        }

        if (!await db.CounterpartyGroups.AnyAsync(ct))
            foreach (var name in DefaultCounterpartyGroups) db.CounterpartyGroups.Add(new CounterpartyGroup { Name = name });

        if (!await db.Categories.AnyAsync(ct))
            db.Categories.Add(new Category { Name = "Общая" });

        // Статьи доходов/расходов: стартовый набор (дальше ведутся в справочнике)
        if (!await db.FinanceArticles.AnyAsync(ct))
        {
            foreach (var name in FinanceService.DefaultIncomeArticles)
                db.FinanceArticles.Add(new FinanceArticle { Name = name, Direction = CashDirection.Income });
            foreach (var name in FinanceService.DefaultExpenseArticles)
                db.FinanceArticles.Add(new FinanceArticle { Name = name, Direction = CashDirection.Expense });

            // Существующая база: статьи, которые уже вводились вручную в операциях, тоже попадают в справочник
            var used = await db.CashTransactions.AsNoTracking()
                .Where(t => t.Kind == CashOperationKind.Manual && t.Category != "")
                .Select(t => new { t.Direction, t.Category }).Distinct().ToListAsync(ct);
            foreach (var u in used)
            {
                var known = u.Direction == CashDirection.Income ? FinanceService.DefaultIncomeArticles : FinanceService.DefaultExpenseArticles;
                if (!known.Contains(u.Category))
                    db.FinanceArticles.Add(new FinanceArticle { Name = u.Category, Direction = u.Direction });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Встроенные роли «Администратор», «Менеджер», «Кассир». Для существующей базы пользователи
    /// получают роль по прежнему полю Role, а ранее настроенные права разделов переносятся в разрешения.
    /// </summary>
    private static async Task SeedRolesAsync(TradeDbContext db, CancellationToken ct)
    {
        await db.AccessRoles.LoadAsync(ct);
        if (db.AccessRoles.Local.Count > 0)
        {
            // Права разделены по вкладкам и подразделам — переносим прежние настройки ролей
            foreach (var r in db.AccessRoles.Local.Where(r => !r.IsAdmin))
                if (PermissionCatalog.Upgrade(r.Permissions) is { } upgraded) r.Permissions = upgraded;
            return;
        }

        var company = db.CompanySettings.Local.FirstOrDefault() ?? await db.CompanySettings.FirstOrDefaultAsync(ct);
        var admin = new AccessRole { Name = "Администратор", IsAdmin = true };
        var manager = new AccessRole
        {
            Name = "Менеджер",
            Permissions = company?.ManagerPermissions is { } m ? PermissionCatalog.FromLegacy(m) : PermissionCatalog.DefaultManager
        };
        var cashier = new AccessRole
        {
            Name = "Кассир",
            Permissions = company?.CashierPermissions is { } c ? PermissionCatalog.FromLegacy(c) : PermissionCatalog.DefaultCashier
        };
        db.AccessRoles.AddRange(admin, manager, cashier);

        foreach (var u in await db.Users.Where(u => u.AccessRoleId == null).ToListAsync(ct))
            u.AccessRole = u.Role switch { UserRole.Admin => admin, UserRole.Manager => manager, _ => cashier };
    }
}
