using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Data;

/// <summary>Создаёт/обновляет схему локальной базы и заполняет начальные данные.</summary>
public static class DbInitializer
{
    public const string DefaultAdminLogin = "admin";
    public const string DefaultAdminPassword = "admin";

    public static async Task InitializeAsync(TradeDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await SeedAsync(db, ct);
    }

    public static async Task SeedAsync(TradeDbContext db, CancellationToken ct = default)
    {
        if (!await db.CompanySettings.AnyAsync(ct))
            db.CompanySettings.Add(new CompanySettings());

        if (!await db.Users.AnyAsync(ct))
            db.Users.Add(new User
            {
                Login = DefaultAdminLogin,
                FullName = "Администратор",
                Role = UserRole.Admin,
                PasswordHash = PasswordHasher.Hash(DefaultAdminPassword)
            });

        if (!await db.Warehouses.AnyAsync(ct))
            db.Warehouses.Add(new Warehouse { Name = "Основной склад" });

        if (!await db.CashRegisters.AnyAsync(ct))
            db.CashRegisters.Add(new CashRegister { Name = "Основная касса" });

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
}
