using System.Globalization;
using System.Text;
using MiniTrade.Core.Common;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Services;

/// <summary>Итог импорта: добавлено, обновлено, пропущено (с причинами по строкам).</summary>
public sealed record ImportResult(int Added, int Updated, IReadOnlyList<string> Errors);

/// <summary>
/// Импорт и экспорт справочников «Товары» и «Контрагенты» в CSV (разделитель «;», UTF-8 с BOM — открывается в Excel).
/// Колонки ищутся по заголовку, поэтому порядок в файле не важен. Существующие записи обновляются:
/// товар — по штрихкоду, затем артикулу, затем наименованию; контрагент — по наименованию.
/// Отсутствующие категории и группы создаются.
/// </summary>
public class CatalogTransferService(IDbContextFactory<TradeDbContext> factory)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static readonly string[] ProductColumns =
        ["Наименование", "Артикул", "Штрихкод", "Категория", "Ед.", "Закупка", "Продажа", "Мин. остаток", "Активен"];

    public static readonly string[] CounterpartyColumns =
        ["Наименование", "Группа", "Телефон", "Адрес", "Примечание", "Активен"];

    // ================= Экспорт / шаблон =================

    public static IEnumerable<string> ProductTemplate() =>
    [
        Row(ProductColumns),
        Row("Кока-кола 0,5", Code("CC05"), Code("4600000000017"), "Напитки", "шт", 8m, 10m, 5m, "Да")
    ];

    public static IEnumerable<string> CounterpartyTemplate() =>
    [
        Row(CounterpartyColumns),
        Row("ООО «Пример»", "Поставщики", "+992 900 00 00 00", "г. Душанбе", "", "Да")
    ];

    public async Task<List<string>> ExportProductsAsync(bool includeArchived, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var items = await db.Products.AsNoTracking().Include(p => p.Category)
            .Where(p => !p.IsDeleted && (includeArchived || p.IsActive)).OrderBy(p => p.Name).ToListAsync(ct);
        var lines = new List<string> { Row(ProductColumns) };
        lines.AddRange(items.Select(p => Row(p.Name, Code(p.Sku), Code(p.Barcode), p.Category?.Name, p.Unit,
            p.PurchasePrice, p.SalePrice, p.MinStock, p.IsActive ? "Да" : "Нет")));
        return lines;
    }

    public async Task<List<string>> ExportCounterpartiesAsync(bool includeArchived, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var items = await db.Counterparties.AsNoTracking().Include(c => c.Group)
            .Where(c => !c.IsDeleted && (includeArchived || c.IsActive)).OrderBy(c => c.Name).ToListAsync(ct);
        var lines = new List<string> { Row(CounterpartyColumns) };
        lines.AddRange(items.Select(c => Row(c.Name, c.Group?.Name, c.Phone, c.Address, c.Note, c.IsActive ? "Да" : "Нет")));
        return lines;
    }

    // ================= Импорт =================

    public async Task<ImportResult> ImportProductsAsync(string text, CancellationToken ct = default)
    {
        var (header, rows) = Parse(text);
        var col = MapColumns(header, ProductColumns);
        if (col["Наименование"] < 0) throw new BusinessException("В файле нет колонки «Наименование».");

        await using var db = await factory.CreateDbContextAsync(ct);
        var products = await db.Products.Where(p => !p.IsDeleted).ToListAsync(ct);
        var categories = await db.Categories.Where(c => !c.IsDeleted).ToListAsync(ct);
        var byBarcode = Index(products, p => p.Barcode);
        var bySku = Index(products, p => p.Sku);
        var byName = Index(products, p => p.Name);

        int added = 0, updated = 0;
        var errors = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            string? Get(string name) => Cell(r, col[name]);
            var line = i + 2;
            var name = Get("Наименование");
            if (string.IsNullOrWhiteSpace(name)) { if (r.Any(c => c.Length > 0)) errors.Add($"Строка {line}: не указано наименование."); continue; }

            try
            {
                var barcode = Get("Штрихкод");
                var sku = Get("Артикул");
                var p = (barcode is not null ? byBarcode.GetValueOrDefault(barcode) : null)
                        ?? (sku is not null ? bySku.GetValueOrDefault(sku) : null)
                        ?? byName.GetValueOrDefault(name);
                var isNew = p is null;
                p ??= new Product();

                p.Name = name;
                if (col["Артикул"] >= 0) p.Sku = sku;
                if (col["Штрихкод"] >= 0) p.Barcode = barcode;
                if (col["Ед."] >= 0) p.Unit = Get("Ед.") ?? "шт";
                if (col["Закупка"] >= 0) p.PurchasePrice = Money.Round(Dec(Get("Закупка"), "Закупка"));
                if (col["Продажа"] >= 0) p.SalePrice = Money.Round(Dec(Get("Продажа"), "Продажа"));
                if (col["Мин. остаток"] >= 0) p.MinStock = Dec(Get("Мин. остаток"), "Мин. остаток");
                if (col["Активен"] >= 0) p.IsActive = Bool(Get("Активен"));
                if (col["Категория"] >= 0)
                {
                    var catName = Get("Категория");
                    if (catName is null) p.CategoryId = null;
                    else
                    {
                        var cat = categories.FirstOrDefault(c => string.Equals(c.Name, catName, StringComparison.OrdinalIgnoreCase));
                        if (cat is null) { cat = new Category { Name = catName }; categories.Add(cat); db.Categories.Add(cat); }
                        p.Category = cat;
                    }
                }

                if (isNew)
                {
                    db.Products.Add(p);
                    products.Add(p);
                    added++;
                }
                else updated++;
                if (p.Barcode is not null) byBarcode[p.Barcode] = p;
                if (p.Sku is not null) bySku[p.Sku] = p;
                byName[p.Name] = p;
            }
            catch (FormatException ex) { errors.Add($"Строка {line}: {ex.Message}"); }
        }
        await db.SaveChangesAsync(ct);
        return new ImportResult(added, updated, errors);
    }

    public async Task<ImportResult> ImportCounterpartiesAsync(string text, CancellationToken ct = default)
    {
        var (header, rows) = Parse(text);
        var col = MapColumns(header, CounterpartyColumns);
        if (col["Наименование"] < 0) throw new BusinessException("В файле нет колонки «Наименование».");

        await using var db = await factory.CreateDbContextAsync(ct);
        var items = await db.Counterparties.Where(c => !c.IsDeleted).ToListAsync(ct);
        var groups = await db.CounterpartyGroups.Where(g => !g.IsDeleted).ToListAsync(ct);
        var byName = Index(items, c => c.Name);

        int added = 0, updated = 0;
        var errors = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            string? Get(string name) => Cell(r, col[name]);
            var name = Get("Наименование");
            if (string.IsNullOrWhiteSpace(name)) { if (r.Any(c => c.Length > 0)) errors.Add($"Строка {i + 2}: не указано наименование."); continue; }

            var c = byName.GetValueOrDefault(name);
            var isNew = c is null;
            c ??= new Counterparty();
            c.Name = name;
            if (col["Телефон"] >= 0) c.Phone = Get("Телефон");
            if (col["Адрес"] >= 0) c.Address = Get("Адрес");
            if (col["Примечание"] >= 0) c.Note = Get("Примечание");
            if (col["Активен"] >= 0) c.IsActive = Bool(Get("Активен"));
            if (col["Группа"] >= 0)
            {
                var groupName = Get("Группа");
                if (groupName is null) c.GroupId = null;
                else
                {
                    var g = groups.FirstOrDefault(x => string.Equals(x.Name, groupName, StringComparison.OrdinalIgnoreCase));
                    if (g is null) { g = new CounterpartyGroup { Name = groupName }; groups.Add(g); db.CounterpartyGroups.Add(g); }
                    c.Group = g;
                }
            }
            if (isNew) { db.Counterparties.Add(c); added++; } else updated++;
            byName[c.Name] = c;
        }
        await db.SaveChangesAsync(ct);
        return new ImportResult(added, updated, errors);
    }

    // ================= CSV =================

    private static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string?> key)
    {
        var d = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items)
            if (key(i) is { Length: > 0 } k) d.TryAdd(k.Trim(), i);
        return d;
    }

    private static Dictionary<string, int> MapColumns(IReadOnlyList<string> header, string[] columns)
    {
        static string Norm(string s) => s.Trim().TrimEnd('.').ToLowerInvariant();
        return columns.ToDictionary(c => c, c =>
        {
            for (var i = 0; i < header.Count; i++)
                if (Norm(header[i]) == Norm(c)) return i;
            return -1;
        });
    }

    private static string? Cell(IReadOnlyList<string> row, int index) =>
        index >= 0 && index < row.Count && Unwrap(row[index].Trim()) is { Length: > 0 } v ? v : null;

    /// <summary>Значение вида ="0123" (так экспортируются коды, чтобы Excel не портил ведущие нули и длинные числа).</summary>
    private static string Unwrap(string v) =>
        v.Length >= 3 && v.StartsWith("=\"") && v.EndsWith('"') ? v[2..^1].Trim() : v;

    private static string? Code(string? v) => string.IsNullOrEmpty(v) ? null : $"=\"{v}\"";

    private static decimal Dec(string? s, string column)
    {
        if (s is null) return 0;
        var norm = s.Replace(" ", "").Replace(" ", "").Replace(',', '.');
        if (decimal.TryParse(norm, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0) return d;
        throw new FormatException($"неверное число «{s}» в колонке «{column}».");
    }

    private static bool Bool(string? s) => s is null || !(s.Trim().ToLowerInvariant() is "нет" or "no" or "0" or "false" or "не");

    /// <summary>Разбор CSV: разделитель (; , или табуляция) — по строке заголовка; поддерживаются кавычки и переносы внутри них.</summary>
    public static (List<string> Header, List<List<string>> Rows) Parse(string text)
    {
        text = text.TrimStart('﻿');
        var firstLine = text.Split('\n', 2)[0];
        var sep = new[] { ';', '\t', ',' }.OrderByDescending(c => firstLine.Count(x => x == c)).First();

        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new(); }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        rows.RemoveAll(r => r.All(c => c.Trim().Length == 0));
        if (rows.Count == 0) throw new BusinessException("Файл пуст.");
        return (rows[0], rows.Skip(1).ToList());
    }

    private static string Row(params object?[] values) => string.Join(';', values.Select(v => v switch
    {
        decimal d => d.ToString("0.###", Ru),
        null => "",
        _ => '"' + v.ToString()!.Replace("\"", "\"\"") + '"'
    }));
}
