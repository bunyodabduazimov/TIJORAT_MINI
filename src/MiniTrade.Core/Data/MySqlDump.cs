using System.Globalization;
using System.Text;
using MySql.Data.MySqlClient;

namespace MiniTrade.Core.Data;

/// <summary>
/// Ручной SQL-дамп базы MySQL (структура + данные) без внешнего mysqldump,
/// и восстановление базы из такого дампа.
/// </summary>
public static class MySqlDump
{
    public const string Header = "-- TIJORAT MINI MySQL dump";

    private static string ConnectionString(DatabaseConnection c) => new MySqlConnectionStringBuilder(c.MySqlConnectionString)
    {
        ConvertZeroDateTime = true,
        DefaultCommandTimeout = 600,
        CharacterSet = "utf8mb4"
    }.ConnectionString;

    /// <summary>Сохраняет все таблицы базы в SQL-файл.</summary>
    public static async Task ExportAsync(DatabaseConnection c, string path, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(ConnectionString(c));
        await conn.OpenAsync(ct);

        var tables = new List<string>();
        await using (var cmd = new MySqlCommand("SHOW FULL TABLES WHERE Table_type = 'BASE TABLE'", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) tables.Add(r.GetString(0));

        var tmp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
        {
            await w.WriteLineAsync($"{Header} {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            await w.WriteLineAsync($"-- Database: {c.Database}");
            await w.WriteLineAsync("SET NAMES utf8mb4;");
            await w.WriteLineAsync("SET FOREIGN_KEY_CHECKS = 0;");
            await w.WriteLineAsync("SET SQL_MODE = 'NO_AUTO_VALUE_ON_ZERO';");
            await w.WriteLineAsync();

            foreach (var table in tables)
            {
                ct.ThrowIfCancellationRequested();
                var q = Quote(table);
                string create;
                await using (var cmd = new MySqlCommand($"SHOW CREATE TABLE {q}", conn))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    await r.ReadAsync(ct);
                    create = r.GetString(1);
                }
                await w.WriteLineAsync($"-- Table {table}");
                await w.WriteLineAsync($"DROP TABLE IF EXISTS {q};");
                await w.WriteLineAsync(create + ";");

                await using (var cmd = new MySqlCommand($"SELECT * FROM {q}", conn))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    var cols = string.Join(", ", Enumerable.Range(0, r.FieldCount).Select(i => Quote(r.GetName(i))));
                    var batch = new StringBuilder();
                    var count = 0;
                    while (await r.ReadAsync(ct))
                    {
                        batch.Append(count == 0 ? $"INSERT INTO {q} ({cols}) VALUES\n(" : ",\n(");
                        for (var i = 0; i < r.FieldCount; i++)
                        {
                            if (i > 0) batch.Append(", ");
                            batch.Append(Literal(r.IsDBNull(i) ? null : r.GetValue(i)));
                        }
                        batch.Append(')');
                        if (++count == 200 || batch.Length > 512 * 1024)
                        {
                            await w.WriteLineAsync(batch.Append(';').ToString());
                            batch.Clear();
                            count = 0;
                        }
                    }
                    if (count > 0) await w.WriteLineAsync(batch.Append(';').ToString());
                }
                await w.WriteLineAsync();
            }
            await w.WriteLineAsync("SET FOREIGN_KEY_CHECKS = 1;");
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Проверка, что файл — дамп TIJORAT (заголовок сохранён для совместимости).</summary>
    public static void Validate(string path)
    {
        using var r = new StreamReader(path);
        if (r.ReadLine()?.StartsWith(Header, StringComparison.Ordinal) != true)
            throw new InvalidDataException("not a dump");
    }

    /// <summary>Заменяет содержимое базы данными из SQL-дампа.</summary>
    public static async Task ImportAsync(DatabaseConnection c, string path, CancellationToken ct = default)
    {
        Validate(path);
        var sql = await File.ReadAllTextAsync(path, ct);
        await using var conn = new MySqlConnection(ConnectionString(c));
        await conn.OpenAsync(ct);
        var script = new MySqlScript(conn, sql);
        await script.ExecuteAsync(ct);
        MySqlConnection.ClearAllPools();
    }

    private static string Quote(string name) => "`" + name.Replace("`", "``") + "`";

    private static string Literal(object? v) => v switch
    {
        null => "NULL",
        bool b => b ? "1" : "0",
        byte[] bytes => bytes.Length == 0 ? "''" : "0x" + Convert.ToHexString(bytes),
        DateTime d => $"'{d:yyyy-MM-dd HH:mm:ss.ffffff}'",
        DateTimeOffset d => $"'{d.UtcDateTime:yyyy-MM-dd HH:mm:ss.ffffff}'",
        TimeSpan t => $"'{(t < TimeSpan.Zero ? "-" : "")}{(int)Math.Abs(t.TotalHours):00}:{Math.Abs(t.Minutes):00}:{Math.Abs(t.Seconds):00}.{Math.Abs(t.Ticks % TimeSpan.TicksPerSecond) / 10:000000}'",
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        Guid g => $"'{g}'",
        _ => "'" + MySqlHelper.EscapeString(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "") + "'"
    };
}
