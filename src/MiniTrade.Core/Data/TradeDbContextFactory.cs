using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using MySql.Data.MySqlClient;

namespace MiniTrade.Core.Data;

/// <summary>Фабрика для инструментов EF Core (dotnet ef migrations) — миграции SQLite.</summary>
public class DesignTimeTradeDbContextFactory : IDesignTimeDbContextFactory<TradeDbContext>
{
    public TradeDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TradeDbContext>().UseSqlite("Data Source=design.db").Options);
}

/// <summary>Фабрика для инструментов EF Core — миграции MySQL (подключение к серверу не требуется).</summary>
public class DesignTimeMySqlTradeDbContextFactory : IDesignTimeDbContextFactory<MySqlTradeDbContext>
{
    public MySqlTradeDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MySqlTradeDbContext>().UseMySQL("Server=localhost;Database=minitrade").Options);
}

public enum DatabaseProvider
{
    /// <summary>Локальный файл SQLite на этом компьютере (по умолчанию).</summary>
    Sqlite,
    /// <summary>Общая база на сервере MySQL — для работы с нескольких устройств.</summary>
    MySql
}

/// <summary>Параметры подключения к базе.</summary>
public sealed record DatabaseConnection
{
    public DatabaseProvider Provider { get; init; } = DatabaseProvider.Sqlite;
    /// <summary>Путь к файлу SQLite (для <see cref="DatabaseProvider.Sqlite"/>).</summary>
    public string SqlitePath { get; init; } = TradeDatabase.DefaultPath;
    public string Host { get; init; } = "";
    public int Port { get; init; } = 3306;
    public string Database { get; init; } = "minitrade";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";

    public bool IsRemote => Provider != DatabaseProvider.Sqlite;

    public string MySqlConnectionString => new MySqlConnectionStringBuilder
    {
        Server = Host,
        Port = (uint)Port,
        Database = Database,
        UserID = Username,
        Password = Password,
        ConnectionTimeout = 10
    }.ConnectionString;

    /// <summary>Описание для интерфейса (без пароля).</summary>
    public string Display => IsRemote ? $"MySQL: {Username}@{Host}:{Port}/{Database}" : SqlitePath;
}

/// <summary>Пути и параметры подключения к базе.</summary>
public static class TradeDatabase
{
    /// <summary>%LocalAppData%\MiniTrade\minitrade.db — у каждой установки своя база.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniTrade", "minitrade.db");

    public static void Configure(DbContextOptionsBuilder builder, string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        builder.UseSqlite($"Data Source={dbPath};Foreign Keys=True");
    }

    public static TradeDbContext Create(DatabaseConnection c) => c.Provider switch
    {
        DatabaseProvider.MySql => new MySqlTradeDbContext(new DbContextOptionsBuilder<MySqlTradeDbContext>()
            .UseMySQL(c.MySqlConnectionString).Options),
        _ => new TradeDbContext(Configured(c.SqlitePath))
    };

    private static DbContextOptions<TradeDbContext> Configured(string path)
    {
        var b = new DbContextOptionsBuilder<TradeDbContext>();
        Configure(b, path);
        return b.Options;
    }

    /// <summary>Проверка подключения к серверу: доступность и права. Возвращает null при успехе, иначе текст ошибки.</summary>
    public static async Task<string?> TestAsync(DatabaseConnection c, CancellationToken ct = default)
    {
        try
        {
            await using var db = Create(c);
            await db.Database.OpenConnectionAsync(ct);
            return null;
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            return ex.InnerException?.Message is { } inner && ex is InvalidOperationException ? inner : ex.Message;
        }
    }
}

/// <summary>Фабрика контекстов для выбранного подключения (SQLite или MySQL).</summary>
public sealed class TradeDbContextFactory(DatabaseConnection connection) : IDbContextFactory<TradeDbContext>
{
    public DatabaseConnection Connection => connection;
    public TradeDbContext CreateDbContext() => TradeDatabase.Create(connection);
}
