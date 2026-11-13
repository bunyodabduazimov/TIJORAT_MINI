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
        return await db.CompanySettings.AsNoTracking().FirstAsync(ct);
    }

    public async Task SaveCompanyAsync(CompanySettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Name)) throw new BusinessException("Укажите название компании.");
        settings.Currency = Money.Currency;
        await using var db = await factory.CreateDbContextAsync(ct);
        db.CompanySettings.Update(settings);
        await db.SaveChangesAsync(ct);
    }

    public async Task<User?> AuthenticateAsync(string login, string password, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Login == login.Trim().ToLower() && u.IsActive, ct);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash)) return null;
        session.SignIn(user);
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
        return await db.Users.AsNoTracking().OrderBy(u => u.Login).ToListAsync(ct);
    }

    /// <summary>Создание или изменение пользователя. Пароль меняется, если передан.</summary>
    public async Task SaveUserAsync(User user, string? newPassword, CancellationToken ct = default)
    {
        if (!session.IsAdmin) throw new BusinessException("Управлять пользователями может только администратор.");
        if (string.IsNullOrWhiteSpace(user.Login)) throw new BusinessException("Укажите логин.");
        if (string.IsNullOrWhiteSpace(user.FullName)) throw new BusinessException("Укажите ФИО.");
        user.Login = user.Login.Trim().ToLower();
        user.FullName = user.FullName.Trim();
        user.Phone = string.IsNullOrWhiteSpace(user.Phone) ? null : user.Phone.Trim();
        user.Note = string.IsNullOrWhiteSpace(user.Note) ? null : user.Note.Trim();
        if (user.Role == UserRole.Admin || user.Permissions == RolePermissions.Default(user.Role)) user.Permissions = null;
        if (user.Id == session.UserId && (user.Role != UserRole.Admin || !user.IsActive))
            throw new BusinessException("Нельзя снять права администратора или отключить самого себя.");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(u => u.Login == user.Login && u.Id != user.Id, ct))
            throw new BusinessException("Пользователь с таким логином уже существует.");

        if (user.Id == 0)
        {
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 4)
                throw new BusinessException("Пароль должен содержать не менее 4 символов.");
            user.PasswordHash = PasswordHasher.Hash(newPassword);
            db.Users.Add(user);
        }
        else
        {
            var existing = await db.Users.FirstAsync(u => u.Id == user.Id, ct);
            if (existing.Role == UserRole.Admin && (user.Role != UserRole.Admin || !user.IsActive)
                && await db.Users.CountAsync(u => u.Role == UserRole.Admin && u.IsActive, ct) <= 1)
                throw new BusinessException("Должен остаться хотя бы один активный администратор.");
            existing.Login = user.Login;
            existing.FullName = user.FullName;
            existing.Role = user.Role;
            existing.IsActive = user.IsActive;
            existing.Phone = user.Phone;
            existing.Note = user.Note;
            existing.Permissions = user.Role == UserRole.Admin ? null : user.Permissions;
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
        if (!session.IsAdmin) throw new BusinessException("Управлять пользователями может только администратор.");
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
}
