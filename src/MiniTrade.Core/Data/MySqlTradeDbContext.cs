using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MiniTrade.Core.Data;

/// <summary>
/// Та же модель на сервере MySQL (работа нескольких компьютеров с одной базой).
/// Отдельный тип нужен для собственного набора миграций (Data/MySqlMigrations).
/// </summary>
public sealed class MySqlTradeDbContext(DbContextOptions<MySqlTradeDbContext> options) : TradeDbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        // Совместимость с MariaDB (XAMPP): своя блокировка миграций
        optionsBuilder.ReplaceService<IHistoryRepository, MariaDbCompatibleHistoryRepository>();
    }
}
