using MiniTrade.Core.Common;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Services;

/// <summary>Настройки компании и управление пользователями.</summary>
public class SettingsService(IDbContextFactory<TradeDbContext> factory, UserSession session)
{
    public async Task<CompanySettings> GetCompanyAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var company = await db.CompanySettings.AsNoTracking().FirstAsync(ct);
        company.Currency = Money.SetCurrency(company.Currency);
        Countries.SetCurrent(company.Country);
        return company;
    }

    public async Task SaveCompanyAsync(CompanySettings settings, CancellationToken ct = default)
    {
        Access.Demand(Perm.SettingsCompany);
        if (string.IsNullOrWhiteSpace(settings.Name)) throw new BusinessException("Укажите название компании.");
        var currency = (settings.Currency ?? "").Trim().ToUpperInvariant();
        if (currency.Length > 10) throw new BusinessException("Код валюты — не длиннее 10 символов (например, TJS, RUB, UZS).");
        settings.ScalePrefix = string.IsNullOrWhiteSpace(settings.ScalePrefix) ? null : settings.ScalePrefix.Trim();
        if (settings.ScalePrefix is { } prefix && (prefix.Length != 2 || !prefix.All(char.IsAsciiDigit)))
            throw new BusinessException("Код весов — ровно 2 цифры (например, 20) или пустое поле, если весов нет.");
        await using var db = await factory.CreateDbContextAsync(ct);
        settings.Currency = currency.Length == 0 ? Money.DefaultCurrency : currency;
        db.CompanySettings.Update(settings);
        await db.SaveChangesAsync(ct);
        Money.SetCurrency(settings.Currency);
        Countries.SetCurrent(settings.Country);
    }

    public async Task<User?> AuthenticateAsync(string login, string password, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.Include(u => u.AccessRole)
            .FirstOrDefaultAsync(u => u.Login == login.Trim().ToLower() && u.IsActive, ct);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash)) return null;
        if (user.IsAccessExpired)
            throw new BusinessException("Срок доступа истёк {0:dd.MM.yyyy}. Обратитесь к администратору.", user.AccessUntil);
        if (!user.IsWithinWorkHours(DateTime.Now.TimeOfDay))
            throw new BusinessException("Вход разрешён только с {0} до {1}. Обратитесь к администратору.", user.WorkFrom?.ToString(@"hh\:mm"), user.WorkTo?.ToString(@"hh\:mm"));
        session.SignIn(user);
        var company = await db.CompanySettings.AsNoTracking().Select(c => new { c.Currency, c.Country }).FirstOrDefaultAsync(ct);
        Money.SetCurrency(company?.Currency);
        Countries.SetCurrent(company?.Country);
        user.LastLoginAt = DateTime.Now;
        user.LastLoginMachine = Environment.MachineName;
        user.LoginCount++;
        db.AuditLogs.Add(new AuditLog
        {
            UserId = user.Id, UserName = AuditContext.UserName, Machine = Environment.MachineName,
            Action = AuditAction.Login, EntityType = "Session", EntityName = "Вход в программу"
        });
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<List<User>> GetUsersAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().Include(u => u.AccessRole).OrderBy(u => u.Login).ToListAsync(ct);
    }

    // ================= Роли =================

    public async Task<List<AccessRole>> GetRolesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var roles = await db.AccessRoles.AsNoTracking().ToListAsync(ct);
        return roles.OrderByDescending(r => r.IsAdmin).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Количество пользователей по ролям.</summary>
    public async Task<Dictionary<int, int>> GetRoleUserCountsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.Where(u => u.AccessRoleId != null).GroupBy(u => u.AccessRoleId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    public async Task<AccessRole> SaveRoleAsync(AccessRole role, IEnumerable<string> permissions, CancellationToken ct = default)
    {
        Access.Demand(Perm.SettingsRoles);
        var name = role.Name?.Trim() ?? "";
        if (name.Length == 0) throw new BusinessException("Укажите название роли.");
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.AccessRoles.AnyAsync(r => r.Name == name && r.Id != role.Id, ct))
            throw new BusinessException("Роль «{0}» уже существует.", name);

        AccessRole entity;
        if (role.Id == 0) db.AccessRoles.Add(entity = new AccessRole());
        else entity = await db.AccessRoles.FindAsync([role.Id], ct) ?? throw new BusinessException("Роль не найдена.");


        entity.Name = name;
        entity.Description = string.IsNullOrWhiteSpace(role.Description) ? null : role.Description.Trim();
        // У администратора все права — список не храним
        entity.Permissions = entity.IsAdmin ? "" : PermissionCatalog.Pack(permissions);
        await db.SaveChangesAsync(ct);
        RefreshSessionRole(entity);
        return entity;
    }

    public async Task DeleteRoleAsync(int id, CancellationToken ct = default)
    {
        Access.Demand(Perm.SettingsRoles);
        await using var db = await factory.CreateDbContextAsync(ct);
        var role = await db.AccessRoles.FindAsync([id], ct) ?? throw new BusinessException("Роль не найдена.");
        if (role.IsAdmin) throw new BusinessException("Роль администратора удалить нельзя.");
        var users = await db.Users.CountAsync(u => u.AccessRoleId == id, ct);
        if (users > 0) throw new BusinessException("Роль назначена пользователям ({0}). Сначала смените им роль.", users);
        db.AccessRoles.Remove(role);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Изменили роль текущего пользователя — права применяются сразу.</summary>
    private void RefreshSessionRole(AccessRole role)
    {
        if (session.CurrentUser is { } u && u.AccessRoleId == role.Id)
            u.AccessRole = new AccessRole { Id = role.Id, Name = role.Name, IsAdmin = role.IsAdmin, Permissions = role.Permissions };
    }

    // ================= Пользователи =================

    /// <summary>Создание или изменение пользователя. Пароль меняется, если передан. Права определяет роль.</summary>
    public async Task SaveUserAsync(User user, string? newPassword, CancellationToken ct = default)
    {
        Access.Demand(Perm.SettingsUsers);
        if (string.IsNullOrWhiteSpace(user.Login)) throw new BusinessException("Укажите логин.");
        if (string.IsNullOrWhiteSpace(user.FullName)) throw new BusinessException("Укажите ФИО.");
        if (user.AccessRoleId is null) throw new BusinessException("Выберите роль.");
        user.Login = user.Login.Trim().ToLower();
        user.FullName = user.FullName.Trim();
        user.Phone = string.IsNullOrWhiteSpace(user.Phone) ? null : user.Phone.Trim();
        user.Note = string.IsNullOrWhiteSpace(user.Note) ? null : user.Note.Trim();
        user.Permissions = null;
        if (user.MaxDiscountPercent is < 0 or > 100) throw new BusinessException("Максимальная скидка — от 0 до 100%.");
        user.AccessUntil = user.AccessUntil?.Date;
        if ((user.WorkFrom is null) != (user.WorkTo is null))
            throw new BusinessException("Укажите начало и конец времени работы или оставьте оба поля пустыми.");
        if (user.WorkFrom is { } wf && wf == user.WorkTo) { user.WorkFrom = null; user.WorkTo = null; }
        if (user.Id == session.UserId && !user.IsWithinWorkHours(DateTime.Now.TimeOfDay))
            throw new BusinessException("Текущее время вне указанного времени работы — вы не сможете войти. Нельзя ограничить самого себя так.");
        // Склад/касса по умолчанию должны входить в доступные
        if (User.ParseIds(user.AllowedWarehouseIds) is { } wh && user.DefaultWarehouseId is { } dw && !wh.Contains(dw))
            throw new BusinessException("Склад по умолчанию должен быть среди доступных складов.");
        if (User.ParseIds(user.AllowedCashRegisterIds) is { } cr && user.DefaultCashRegisterId is { } dc && !cr.Contains(dc))
            throw new BusinessException("Касса по умолчанию должна быть среди доступных касс.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var role = await db.AccessRoles.FindAsync([user.AccessRoleId.Value], ct) ?? throw new BusinessException("Роль не найдена.");
        // Поле Role — признак администратора (все права); остальные права задаёт роль
        user.Role = role.IsAdmin ? UserRole.Admin : UserRole.Cashier;
        if (user.Id == session.UserId && (!role.IsAdmin && session.IsAdmin || !user.IsActive))
            throw new BusinessException("Нельзя снять права администратора или отключить самого себя.");
        if (await db.Users.AnyAsync(u => u.Login == user.Login && u.Id != user.Id, ct))
            throw new BusinessException("Пользователь с таким логином уже существует.");

        if (user.Id == 0)
        {
            if (role.IsAdmin && !session.IsAdmin && session.CurrentUser is not null)
                throw new BusinessException("Назначить роль администратора может только администратор.");
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 4)
                throw new BusinessException("Пароль должен содержать не менее 4 символов.");
            user.PasswordHash = PasswordHasher.Hash(newPassword);
            user.AccessRole = null;
            db.Users.Add(user);
        }
        else
        {
            var existing = await db.Users.FirstAsync(u => u.Id == user.Id, ct);
            if ((existing.Role == UserRole.Admin || role.IsAdmin) && !session.IsAdmin && session.CurrentUser is not null)
                throw new BusinessException("Изменять администраторов может только администратор.");
            if (existing.Role == UserRole.Admin && (!role.IsAdmin || !user.IsActive)
                && await db.Users.CountAsync(u => u.Role == UserRole.Admin && u.IsActive, ct) <= 1)
                throw new BusinessException("Должен остаться хотя бы один активный администратор.");
            existing.Login = user.Login;
            existing.FullName = user.FullName;
            existing.Role = user.Role;
            existing.AccessRoleId = user.AccessRoleId;
            existing.IsActive = user.IsActive;
            existing.Phone = user.Phone;
            existing.Note = user.Note;
            existing.DefaultWarehouseId = user.DefaultWarehouseId;
            existing.DefaultCashRegisterId = user.DefaultCashRegisterId;
            existing.AllowedWarehouseIds = user.AllowedWarehouseIds;
            existing.AllowedCashRegisterIds = user.AllowedCashRegisterIds;
            existing.MaxDiscountPercent = user.MaxDiscountPercent;
            existing.AccessUntil = user.AccessUntil;
            existing.WorkFrom = user.WorkFrom;
            existing.WorkTo = user.WorkTo;
            existing.Permissions = null;
            if (!string.IsNullOrWhiteSpace(newPassword))
            {
                if (newPassword.Length < 4) throw new BusinessException("Пароль должен содержать не менее 4 символов.");
                existing.PasswordHash = PasswordHasher.Hash(newPassword);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Удаление пользователя. Если он уже проводил документы, удалить нельзя (история не должна терять автора) —
    /// такого пользователя следует отключить.
    /// </summary>
    public async Task DeleteUserAsync(int id, CancellationToken ct = default)
    {
        Access.Demand(Perm.SettingsUsers);
        if (id == session.UserId) throw new BusinessException("Нельзя удалить самого себя.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
                   ?? throw new BusinessException("Пользователь не найден.");
        if (user.Role == UserRole.Admin && user.IsActive
            && await db.Users.CountAsync(u => u.Role == UserRole.Admin && u.IsActive, ct) <= 1)
            throw new BusinessException("Должен остаться хотя бы один активный администратор.");
        if (await db.Sales.AnyAsync(x => x.UserId == id, ct) || await db.SaleReturns.AnyAsync(x => x.UserId == id, ct)
            || await db.Purchases.AnyAsync(x => x.UserId == id, ct) || await db.PurchaseReturns.AnyAsync(x => x.UserId == id, ct)
            || await db.CashTransactions.AnyAsync(x => x.UserId == id, ct))
            throw new BusinessException("У пользователя есть документы, поэтому удалить его нельзя. Снимите флажок «Активен», чтобы запретить вход.");
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Смена собственного пароля текущим пользователем (с проверкой старого).</summary>
    public async Task ChangeOwnPasswordAsync(string? oldPassword, string? newPassword, CancellationToken ct = default)
    {
        if (session.UserId is not { } id) throw new BusinessException("Нет активного пользователя.");
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 4)
            throw new BusinessException("Пароль должен содержать не менее 4 символов.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstAsync(u => u.Id == id, ct);
        if (!PasswordHasher.Verify(oldPassword ?? "", user.PasswordHash))
            throw new BusinessException("Текущий пароль указан неверно.");
        user.PasswordHash = PasswordHasher.Hash(newPassword);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Проверка пароля текущего пользователя-администратора перед опасным действием.</summary>
    public async Task VerifyAdminPasswordAsync(string? password, CancellationToken ct = default)
    {
        if (session.CurrentUser is not { IsAdmin: true } || session.UserId is not { } id)
            throw new BusinessException("Очистить базу может только администратор.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstAsync(u => u.Id == id, ct);
        if (!PasswordHasher.Verify(password ?? "", user.PasswordHash))
            throw new BusinessException("Пароль администратора указан неверно.");
    }

    /// <summary>
    /// Полная очистка: база удаляется целиком (SQLite — файл, MySQL — база на сервере).
    /// При следующем запуске она создаётся заново, как новая: администратор admin/admin, основной склад и касса.
    /// Резервную копию делает вызывающий код до очистки.
    /// </summary>
    public async Task WipeDatabaseAsync(string? adminPassword, CancellationToken ct = default)
    {
        Access.Demand(Perm.SettingsDatabase);
        await VerifyAdminPasswordAsync(adminPassword, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.EnsureDeletedAsync(ct);
    }
}
