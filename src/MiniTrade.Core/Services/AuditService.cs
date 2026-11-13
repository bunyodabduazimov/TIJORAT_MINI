using Microsoft.EntityFrameworkCore;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;

namespace MiniTrade.Core.Services;

/// <summary>Журнал действий пользователей: просмотр и запись входа/выхода.</summary>
public class AuditService(IDbContextFactory<TradeDbContext> factory)
{
    /// <summary>Записи за период (новые сверху), с фильтром по пользователю и виду действия.</summary>
    public async Task<List<AuditLog>> GetAsync(DateTime from, DateTime to, int? userId = null, AuditAction? action = null,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var start = from.Date;
        var end = to.Date.AddDays(1);
        var q = db.AuditLogs.AsNoTracking().Where(a => a.Date >= start && a.Date < end);
        if (userId is not null) q = q.Where(a => a.UserId == userId);
        if (action is not null) q = q.Where(a => a.Action == action);
        return await q.OrderByDescending(a => a.Date).ThenByDescending(a => a.Id).ToListAsync(ct);
    }

    /// <summary>Пользователи, которые есть в журнале (для фильтра).</summary>
    public async Task<List<User>> GetUsersAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().OrderBy(u => u.FullName).ToListAsync(ct);
    }

    /// <summary>Вход или выход текущего пользователя.</summary>
    public async Task LogSessionAsync(AuditAction action, CancellationToken ct = default)
    {
        var entry = new AuditLog
        {
            UserId = AuditContext.UserId,
            UserName = AuditContext.UserName,
            Machine = Environment.MachineName,
            Action = action,
            EntityType = "Session",
            EntityName = action == AuditAction.Login ? "Вход в программу" : "Выход из программы"
        };
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AuditLogs.Add(entry);
        await db.SaveChangesAsync(ct);
    }
}
