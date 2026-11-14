using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>
/// Минимальная запись .xlsx (Office Open XML) без сторонних библиотек.
/// Принимает строки в формате CSV («;», кавычки): поля в кавычках — текст,
/// поля без кавычек, похожие на число (ru-RU), — числовые ячейки.
/// </summary>
public static class ExcelExport
{
    public const string Filter = "Excel (*.xlsx)|*.xlsx";

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static void Save(string path, IEnumerable<string> csvLines, string? sheetName = null)
    {
        var rows = csvLines.Select(ParseLine).ToList();
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        Entry(zip, "[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
        Entry(zip, "_rels/.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        Entry(zip, "xl/_rels/workbook.xml.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
        Entry(zip, "xl/workbook.xml",
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Esc(SheetName(sheetName))}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
        // 0 — обычный, 1 — жирный (заголовок), 2 — число с разделителями
        Entry(zip, "xl/styles.xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="#,##0.###"/></numFmts><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="3"><xf/><xf fontId="1" applyFont="1"/><xf numFmtId="164" applyNumberFormat="1"/></cellXfs></styleSheet>""");
        Entry(zip, "xl/worksheets/sheet1.xml", SheetXml(rows));
    }

    private static string SheetXml(List<List<(string Text, bool Quoted)>> rows)
    {
        var colCount = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var widths = new int[colCount];
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");

        var data = new StringBuilder();
        var headerDone = false;
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            // Первая строка из одних текстовых значений (≥2 колонок) — заголовок таблицы
            var isHeader = !headerDone && row.Count > 1 && row.All(c => !IsNumber(c, out _));
            if (isHeader) headerDone = true;
            data.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < row.Count; c++)
            {
                var cell = row[c];
                if (cell.Text.Length == 0) continue;
                var refName = ColName(c) + (r + 1);
                widths[c] = Math.Max(widths[c], Math.Min(cell.Text.Length, 60));
                if (IsNumber(cell, out var num))
                    data.Append($"<c r=\"{refName}\" s=\"2\"><v>{num.ToString(CultureInfo.InvariantCulture)}</v></c>");
                else
                    data.Append($"<c r=\"{refName}\" t=\"inlineStr\"{(isHeader ? " s=\"1\"" : "")}><is><t xml:space=\"preserve\">{Esc(cell.Text)}</t></is></c>");
            }
            data.Append("</row>");
        }

        if (colCount > 0)
        {
            sb.Append("<cols>");
            for (var c = 0; c < colCount; c++)
                sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{Math.Max(8, widths[c] + 2)}\" customWidth=\"1\"/>");
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>").Append(data).Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static bool IsNumber((string Text, bool Quoted) cell, out decimal value)
    {
        value = 0;
        return !cell.Quoted && cell.Text.Length > 0
               && decimal.TryParse(cell.Text, NumberStyles.Number, Ru, out value);
    }

    private static List<(string, bool)> ParseLine(string line)
    {
        var result = new List<(string, bool)>();
        var sb = new StringBuilder();
        var i = 0;
        while (i <= line.Length)
        {
            sb.Clear();
            var quoted = i < line.Length && line[i] == '"';
            if (quoted)
            {
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(line[i++]);
                }
                while (i < line.Length && line[i] != ';') i++;
            }
            else
            {
                while (i < line.Length && line[i] != ';') sb.Append(line[i++]);
            }
            result.Add((sb.ToString(), quoted));
            i++; // пропуск «;»
        }
        if (line.Length == 0) result.Clear();
        return result;
    }

    private static string ColName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26)
            name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    private static string SheetName(string? name)
    {
        var s = string.Concat((name ?? "Лист1").Where(ch => "[]:*?/\\".IndexOf(ch) < 0)).Trim();
        if (s.Length == 0) s = "Лист1";
        return s.Length > 31 ? s[..31] : s;
    }

    private static string Esc(string s)
    {
        var clean = new string(s.Where(XmlConvert.IsXmlChar).ToArray());
        return clean.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    private static void Entry(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
