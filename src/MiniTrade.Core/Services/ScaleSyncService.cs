using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Services;

/// <summary>Итог выгрузки в одни весы.</summary>
public sealed record ScaleSyncResult(string Scale, bool Online, int Sent, IReadOnlyList<string> Errors);

/// <summary>
/// Выгрузка весовых товаров в торговые весы по сети: код весов (штрихкод товара 1…5 цифр),
/// наименование (латиницей) и цена за кг.
/// TMA — текстовая команда на порт 4001; CAS — через драйвер CAScentre_DLL_printScale.dll (кладётся рядом с программой).
/// </summary>
public class ScaleSyncService(IDbContextFactory<TradeDbContext> factory)
{
    public const string CasDriverFile = "CAScentre_DLL_printScale.dll";

    /// <summary>Активные весы из справочника.</summary>
    public async Task<List<ScaleDevice>> GetScalesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Scales.AsNoTracking().Where(s => s.IsActive && !s.IsDeleted).OrderBy(s => s.Name).ToListAsync(ct);
    }

    /// <summary>Весовые товары, изменённые за период (по дате изменения, включительно; null — без границы).</summary>
    public async Task<List<Product>> GetChangedProductsAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Products.AsNoTracking()
            .Where(p => p.IsWeighted && p.IsActive && !p.IsDeleted && p.Barcode != null && p.Barcode != "");
        if (from is { } f) q = q.Where(p => p.UpdatedAt >= f.Date);
        if (to is { } t) { var end = t.Date.AddDays(1); q = q.Where(p => p.UpdatedAt < end); }
        return await q.OrderBy(p => p.UpdatedAt).ThenBy(p => p.Name).ToListAsync(ct);
    }

    /// <summary>Выгрузка всех весовых товаров во все активные весы.</summary>
    public async Task<IReadOnlyList<ScaleSyncResult>> SyncAllAsync(CancellationToken ct = default) =>
        await SyncAsync(null, await GetChangedProductsAsync(null, null, ct), ct);

    /// <summary>Выгрузка указанных товаров в одни весы (scaleId) или во все активные (null).</summary>
    public async Task<IReadOnlyList<ScaleSyncResult>> SyncAsync(int? scaleId, IReadOnlyList<Product> products, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var scales = (await GetScalesAsync(ct)).Where(s => scaleId is null || s.Id == scaleId).ToList();
        var prefix = await db.CompanySettings.AsNoTracking().Select(c => c.ScalePrefix).FirstOrDefaultAsync(ct);
        if (prefix is not { Length: 2 } || !prefix.All(char.IsAsciiDigit)) prefix = "20";

        var items = new List<(Product P, int Code)>();
        foreach (var p in products)
            if (int.TryParse(p.Barcode!.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is > 0 and <= 99999)
                items.Add((p, code));

        var results = new List<ScaleSyncResult>();
        foreach (var scale in scales)
        {
            if (scale.IsFileBased)
            {
                var errs = new List<string>();
                var written = WriteRongta(scale, items, prefix, errs);
                results.Add(new ScaleSyncResult($"{scale.Name} ({scale.FilePath})", true, written, errs));
                continue;
            }
            var title = $"{scale.Name} ({scale.Ip}:{scale.Port})";
            if (!await IsPortOpenAsync(scale.Ip, scale.Port, ct))
            {
                results.Add(new ScaleSyncResult(title, false, 0, []));
                continue;
            }
            var errors = new List<string>();
            var sent = scale.Type == ScaleType.Cas
                ? await Task.Run(() => SendCas(scale, items, errors), ct)
                : await SendTmaAsync(scale, items, prefix, errors, ct);
            results.Add(new ScaleSyncResult(title, true, sent, errors));
        }
        return results;
    }

    /// <summary>Проверка связи с весами: открывается ли TCP-порт.</summary>
    public static Task<bool> TestConnectionAsync(string host, int port, CancellationToken ct = default) =>
        IsPortOpenAsync(host, port, ct);

    private static async Task<bool> IsPortOpenAsync(string host, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(1500);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    // ───────────── TMA ─────────────

    private static async Task<int> SendTmaAsync(ScaleDevice scale, List<(Product P, int Code)> items, string prefix,
        List<string> errors, CancellationToken ct)
    {
        var sent = 0;
        foreach (var (p, code) in items)
        {
            if (code > 9999) { errors.Add($"{p.Name}: код {code} — у весов TMA не больше 4 цифр"); continue; }
            if (p.SalePrice > 9999.99m) { errors.Add($"{p.Name}: цена больше 9999,99"); continue; }
            try
            {
                var id = code.ToString("D4");
                var barcode = prefix + id;
                var name = Transliterate(p.Name).ToUpperInvariant();
                if (name.Length > 12) name = name[..12];
                var cents = (long)Math.Round(p.SalePrice * 100m);
                var price = (cents / 100).ToString("D4") + (cents % 100).ToString("D2");
                var command = $"!0V{id}A0{barcode}{price}000000002020000000000000000000000000000000000000000000000000B{ToAsciiCodes(name)}000C000D000E\r\n";

                using var tcp = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(3000);
                await tcp.ConnectAsync(scale.Ip, scale.Port, cts.Token);
                await Task.Delay(150, ct);
                await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes(command), cts.Token);
                await Task.Delay(150, ct);
                tcp.Client.Shutdown(SocketShutdown.Both);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                errors.Add($"{p.Name}: {ex.Message}");
            }
        }
        return sent;
    }

    /// <summary>Каждый символ — его код (двузначные дополняются нулём слева до трёх цифр).</summary>
    private static string ToAsciiCodes(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            var code = ((int)c).ToString(CultureInfo.InvariantCulture);
            sb.Append(code.Length == 2 ? "0" + code : code);
        }
        return sb.ToString();
    }

    // ───────────── Rongta (RLS1000) ─────────────

    /// <summary>
    /// Файл PLU для программы RLS1000 (по строке на товар, через табуляцию):
    /// номер PLU, наименование, код товара (дважды), 7, цена, 4, префикс штрихкода, далее служебные поля.
    /// Файл перезаписывается; программа RLS1000 загружает его в весы.
    /// </summary>
    private static int WriteRongta(ScaleDevice scale, List<(Product P, int Code)> items, string prefix, List<string> errors)
    {
        try
        {
            var path = scale.FilePath ?? ScaleDevice.RongtaDefaultPath;
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
            foreach (var (p, code) in items)
            {
                var price = p.SalePrice.ToString("0.00", CultureInfo.InvariantCulture);
                var name = p.Name.Replace('\t', ' ').Trim();
                writer.WriteLine($"{code}\t{name}\t{code}\t{code}\t7\t{price}\t4\t{prefix}\t0.000\t15\t0\t0.000\t0\t0\t0\t0\t0\t0\t0\t0\t0\t0\t0\t0\t0\t0\t0");
            }
            return items.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            errors.Add($"Не удалось записать файл PLU: {ex.Message}");
            return 0;
        }
    }

    // ───────────── CAS ─────────────

    /// <summary>Драйвер CAS загружается при первой выгрузке (класс CAScentre_DLL_printScale.Scale).</summary>
    private static Type? _casType;

    private static int SendCas(ScaleDevice scale, List<(Product P, int Code)> items, List<string> errors)
    {
        if (_casType is null)
        {
            var path = Path.Combine(AppContext.BaseDirectory, CasDriverFile);
            if (!File.Exists(path))
            {
                errors.Add($"Не найден драйвер весов CAS: положите {CasDriverFile} в папку программы ({AppContext.BaseDirectory}).");
                return 0;
            }
            try { _casType = Assembly.LoadFrom(path).GetType("CAScentre_DLL_printScale.Scale", throwOnError: true); }
            catch (Exception ex) { errors.Add($"Драйвер CAS не загружен: {ex.Message}"); return 0; }
        }

        dynamic cas = Activator.CreateInstance(_casType!)!;
        var sent = 0;
        try
        {
            cas.Type = 2;
            cas.IP = scale.Ip;
            cas.Port = scale.Port;
            cas.Open();
            foreach (var (p, code) in items)
            {
                try
                {
                    var name = Transliterate(p.Name).ToUpperInvariant();
                    if (name.Length > 24) name = name[..24];
                    var plu = (uint)code;
                    cas.pluType = 1;
                    cas.pluDept = 1;
                    cas.pluNumber = plu;
                    cas.pluName1 = name;
                    cas.pluName2 = name;
                    cas.pluStrLogo = "";
                    cas.pluGroupCode = 0;
                    cas.pluItemCode = plu;
                    cas.pluFixedPrice = 0;
                    cas.pluPrice = (uint)Math.Round(p.SalePrice * 100m);
                    cas.pluWeightTare = 0;
                    cas.pluDatePack = 0;
                    cas.pluTimePack = 0;
                    cas.pluDateLife = 0;
                    cas.pluTimeLife = 0;
                    cas.pluNumberMsg = 0;
                    cas.pluTextMessage = "";
                    cas.SendPlu();
                    sent++;
                    Thread.Sleep(100);
                }
                catch (Exception ex) { errors.Add($"{p.Name}: {CasError(cas, ex)}"); }
            }
        }
        catch (Exception ex) { errors.Add(CasError(cas, ex)); }
        finally
        {
            try { cas.Close(); } catch { /* соединение уже закрыто */ }
        }
        return sent;
    }

    private static string CasError(dynamic cas, Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
        if (ex.GetType().Name == "ScaleException")
        {
            try { return $"ошибка драйвера весов #{cas.ResultCode}"; } catch { /* нет кода */ }
        }
        return ex.Message;
    }

    // ───────────── Транслитерация (весы печатают только латиницу) ─────────────

    private static readonly Dictionary<char, string> Translit = BuildTranslit();

    private static Dictionary<char, string> BuildTranslit()
    {
        const string ru = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
        string[] lat = ["a", "b", "v", "g", "d", "e", "yo", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "h", "c", "ch", "sh", "sch", "j", "i", "j", "e", "yu", "ya"];
        var map = new Dictionary<char, string>();
        for (var i = 0; i < ru.Length; i++)
        {
            map[ru[i]] = lat[i];
            map[char.ToUpperInvariant(ru[i])] = lat[i].ToUpperInvariant();
        }
        // Таджикские буквы
        foreach (var (c, l) in new[] { ('ғ', "gh"), ('ӣ', "i"), ('қ', "q"), ('ӯ', "u"), ('ҳ', "h"), ('ҷ', "j") })
        {
            map[c] = l;
            map[char.ToUpperInvariant(c)] = l.ToUpperInvariant();
        }
        return map;
    }

    public static string Transliterate(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (Translit.TryGetValue(c, out var l)) sb.Append(l);
            else if (c < 128) sb.Append(c);
        }
        return sb.ToString();
    }
}
