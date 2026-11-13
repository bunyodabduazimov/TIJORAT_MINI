using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MiniTrade.Core.Data;

/// <summary>
/// Таблица истории миграций для MySQL/MariaDB.
/// Стандартная реализация MySql.EntityFrameworkCore берёт блокировку через GET_LOCK(..., -1);
/// MariaDB (например, в XAMPP) на отрицательный таймаут возвращает NULL, и миграция падает
/// с «Unable to cast DBNull to Int64». Здесь таймаут конечный — работает и на MySQL, и на MariaDB.
/// </summary>
public sealed class MariaDbCompatibleHistoryRepository(HistoryRepositoryDependencies dependencies) : HistoryRepository(dependencies)
{
    private const string LockName = "__EFMigrationsLock";
    private const int LockTimeoutSeconds = 3600;

    public override LockReleaseBehavior LockReleaseBehavior => LockReleaseBehavior.Connection;

    protected override string ExistsSql =>
        "SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = " +
        $"'{TableName}' LIMIT 1;";

    protected override bool InterpretExistsResult(object? value) => value is not null and not DBNull;

    public override string GetCreateIfNotExistsScript() =>
        $"CREATE TABLE IF NOT EXISTS `{TableName}` (" +
        $"`{MigrationIdColumnName}` varchar(150) NOT NULL, " +
        $"`{ProductVersionColumnName}` varchar(32) NOT NULL, " +
        $"PRIMARY KEY (`{MigrationIdColumnName}`)) CHARACTER SET utf8mb4;";

    // Идемпотентные SQL-скрипты (dotnet ef migrations script --idempotent) не используются
    public override string GetBeginIfNotExistsScript(string migrationId) => throw new NotSupportedException();
    public override string GetBeginIfExistsScript(string migrationId) => throw new NotSupportedException();
    public override string GetEndIfScript() => throw new NotSupportedException();

    public override IMigrationsDatabaseLock AcquireDatabaseLock()
    {
        var result = Execute($"SELECT GET_LOCK('{LockName}', {LockTimeoutSeconds});");
        return result == 1 ? new Lock(this) : throw new TimeoutException("Не удалось получить блокировку миграций базы данных.");
    }

    public override async Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync($"SELECT GET_LOCK('{LockName}', {LockTimeoutSeconds});", cancellationToken);
        return result == 1 ? new Lock(this) : throw new TimeoutException("Не удалось получить блокировку миграций базы данных.");
    }

    private long? Execute(string sql)
    {
        var value = Dependencies.RawSqlCommandBuilder.Build(sql).ExecuteScalar(Parameters());
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private async Task<long?> ExecuteAsync(string sql, CancellationToken ct)
    {
        var value = await Dependencies.RawSqlCommandBuilder.Build(sql).ExecuteScalarAsync(Parameters(), ct);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private Microsoft.EntityFrameworkCore.Storage.RelationalCommandParameterObject Parameters() =>
        new(Dependencies.Connection, null, null, Dependencies.CurrentContext.Context, Dependencies.CommandLogger, Microsoft.EntityFrameworkCore.Diagnostics.CommandSource.Migrations);

    private sealed class Lock(MariaDbCompatibleHistoryRepository repository) : IMigrationsDatabaseLock
    {
        public IHistoryRepository HistoryRepository => repository;

        public void Dispose() => repository.Execute($"SELECT RELEASE_LOCK('{LockName}');");

        public async ValueTask DisposeAsync() =>
            await repository.ExecuteAsync($"SELECT RELEASE_LOCK('{LockName}');", CancellationToken.None);
    }
}
