using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Data;

/// <summary>
/// Та же модель на сервере PostgreSQL (работа нескольких компьютеров с одной базой).
/// Отдельный тип нужен для собственного набора миграций (Data/PostgresMigrations).
/// </summary>
public sealed class PostgresTradeDbContext(DbContextOptions<PostgresTradeDbContext> options) : TradeDbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Программа работает с локальным временем (DateTime.Now) — храним как есть, без часового пояса
        b.Properties<DateTime>().HaveColumnType("timestamp without time zone");
        b.Properties<DateTime?>().HaveColumnType("timestamp without time zone");
    }
}
