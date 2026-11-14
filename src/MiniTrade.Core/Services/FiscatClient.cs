using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniTrade.Core.Services;

/// <summary>Настройки фискального принтера Fiscat на этом компьютере.</summary>
public sealed class FiscalSettings
{
    /// <summary>Пробивать чеки на ККТ при продаже и возврате.</summary>
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8002;
    /// <summary>СНО: GENERAL, SIMPLIFIED1 … SIMPLIFIED6, SPECIAL.</summary>
    public string TaxType { get; set; } = "GENERAL";
    /// <summary>Ставка НДС товаров: STANDARD, REDUCED1 … REDUCED4, ZERO_TAX, ZERO_TAX_EXPORT.</summary>
    public string VatCode { get; set; } = "STANDARD";
    /// <summary>Печатать бумажный чек.</summary>
    public bool PrintSlip { get; set; } = true;
    /// <summary>Открывать денежный ящик при чеке с наличными.</summary>
    public bool OpenDrawer { get; set; }
}

/// <summary>Строка фискального чека: суммы в сомони.</summary>
public sealed record FiscalLine(string Name, decimal Price, decimal Quantity, decimal Amount, bool IsService, string? Barcode);

/// <summary>Ответ ККТ: rc = SUCCESS или код ошибки.</summary>
public sealed record FiscalResult(bool Ok, string Code, string? Message, JsonNode? Data)
{
    public long? FdNumber => Data?["fdNumber"]?.GetValue<long>();
    public string Error => FiscatClient.Describe(Code) + (string.IsNullOrWhiteSpace(Message) ? "" : $" ({Message})");
}

/// <summary>
/// Фискальный принтер Fiscat (протокол 2.9, HTTP REST API: http://IP:8002/api/…).
/// Суммы передаются в дирамах; одновременно принтер выполняет одну задачу (503 — повтор через 0,7 с).
/// </summary>
public sealed class FiscatClient(FiscalSettings settings)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static readonly Dictionary<string, decimal> VatRates = new()
    {
        ["STANDARD"] = 14m, ["REDUCED1"] = 7m, ["REDUCED2"] = 5m, ["REDUCED3"] = 2.5m, ["REDUCED4"] = 10m,
        ["ZERO_TAX"] = 0m, ["ZERO_TAX_EXPORT"] = 0m
    };

    public static long Dirams(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    public Task<FiscalResult> StatusAsync(bool print = false, CancellationToken ct = default) =>
        SendAsync("deviceStatus", new JsonObject { ["formCode"] = "DEVICE_STATUS", ["shouldPrintSlip"] = print }, ct);

    public Task<FiscalResult> OpenShiftAsync(string cashier, CancellationToken ct = default) =>
        SendAsync("openShift", new JsonObject
        {
            ["formCode"] = "OPEN_SHIFT", ["shouldPrintSlip"] = settings.PrintSlip, ["cashier"] = Cut(cashier, 64), ["externalId"] = NewId()
        }, ct);

    public Task<FiscalResult> CloseShiftAsync(string cashier, CancellationToken ct = default) =>
        SendAsync("closeShift", new JsonObject
        {
            ["formCode"] = "CLOSE_SHIFT", ["shouldPrintSlip"] = true, ["cashier"] = Cut(cashier, 64), ["externalId"] = NewId()
        }, ct);

    public Task<FiscalResult> XReportAsync(CancellationToken ct = default) =>
        SendAsync("getXReport", new JsonObject { ["formCode"] = "GET_X_REPORT", ["shouldPrintSlip"] = true }, ct);

    public Task<FiscalResult> PrintLastAsync(CancellationToken ct = default) =>
        SendAsync("getLastFD", new JsonObject { ["formCode"] = "PRINT_LAST_FD", ["shouldPrintSlip"] = true }, ct);

    public Task<FiscalResult> OpenDrawerAsync(CancellationToken ct = default) =>
        SendAsync("openDrawer", new JsonObject { ["formCode"] = "OPEN_DRAWER", ["onTimeout"] = 500, ["offTimeout"] = 100, ["onQuantity"] = 1 }, ct);

    public Task<FiscalResult> AddCashAsync(decimal amount, CancellationToken ct = default) =>
        SendAsync("addCash", new JsonObject { ["formCode"] = "ADD_CASH", ["shouldPrintSlip"] = settings.PrintSlip, ["addAmount"] = Dirams(amount) }, ct);

    public Task<FiscalResult> RemoveCashAsync(decimal amount, CancellationToken ct = default) =>
        SendAsync("removeCash", new JsonObject { ["formCode"] = "REMOVE_CASH", ["shouldPrintSlip"] = settings.PrintSlip, ["removeAmount"] = Dirams(amount) }, ct);

    /// <summary>
    /// Чек прихода (продажа) или возврата прихода. Смена открывается автоматически, если закрыта.
    /// cash/nonCash — оплата (без сдачи), change — сдача наличными.
    /// </summary>
    public async Task<FiscalResult> ReceiptAsync(bool refund, IReadOnlyList<FiscalLine> lines, decimal cash, decimal nonCash,
        decimal change, string cashier, CancellationToken ct = default)
    {
        var products = new JsonArray();
        long total = 0;
        foreach (var l in lines.Where(l => l.Quantity > 0 && l.Amount > 0))
        {
            var sum = Dirams(l.Amount);
            total += sum;
            var p = new JsonObject
            {
                ["name"] = CutBytes(l.Name, 128),
                ["price"] = Math.Max(1, Dirams(l.Price)),
                ["quantity"] = Math.Round(l.Quantity, 3),
                ["commodity"] = l.IsService ? "SERVICE" : "GOODS",
                ["vatCode"] = settings.VatCode,
                ["sum"] = sum
            };
            if (!string.IsNullOrWhiteSpace(l.Barcode))
                p["code"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(l.Barcode.Trim()));
            products.Add(p);
        }
        if (products.Count == 0) return new FiscalResult(false, "INVALID_DOC", "В чеке нет строк", null);

        var nonCashD = Math.Min(Dirams(nonCash), total);
        var cashD = total - nonCashD;
        var rate = VatRates.GetValueOrDefault(settings.VatCode);
        var vat = (long)Math.Round(total * rate / (100m + rate), MidpointRounding.AwayFromZero);

        var doc = new JsonObject
        {
            ["formCode"] = "RECEIPT",
            ["externalId"] = NewId(),
            ["shouldPrintSlip"] = settings.PrintSlip,
            ["operationType"] = refund ? "REVERT_INCOME" : "INCOME",
            ["taxType"] = settings.TaxType,
            ["products"] = products,
            ["receiptSum"] = total,
            ["receiptCash"] = cashD,
            ["receiptNonCash"] = nonCashD,
            ["taxes"] = new JsonObject { ["vats"] = new JsonArray(new JsonObject { ["vatCode"] = settings.VatCode, ["vatSum"] = vat }) }
        };
        if (!refund && change > 0 && cashD > 0) doc["cashChangeAmount"] = Dirams(change);
        if (settings.OpenDrawer && cashD > 0)
            doc["openDrawer"] = new JsonObject { ["onTimeout"] = 500, ["offTimeout"] = 100, ["onQuantity"] = 1 };

        var r = await SendAsync("formReceipt", doc, ct);
        if (r.Code == "SHIFT_MUST_BE_OPENED")
        {
            var open = await OpenShiftAsync(cashier, ct);
            if (!open.Ok) return open;
            doc["externalId"] = NewId();
            r = await SendAsync("formReceipt", doc, ct);
        }
        return r;
    }

    private async Task<FiscalResult> SendAsync(string method, JsonObject body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Host))
            return new FiscalResult(false, "NO_HOST", "Не указан IP-адрес ККТ", null);
        var url = $"http://{settings.Host.Trim()}:{settings.Port}/api/{method}";
        var json = body.ToJsonString();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync(url, content, ct);
                if (resp.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 8)
                {
                    await Task.Delay(700, ct);
                    continue;
                }
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    return new FiscalResult(false, "HTTP_" + (int)resp.StatusCode, text, null);
                var node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
                var rc = node?["rc"]?.GetValue<string>();
                // Печать ФД отвечает документом без rc
                if (rc is null) return new FiscalResult(true, "SUCCESS", null, node);
                if (rc == "PRINTER_IS_BUSY" && attempt < 8)
                {
                    await Task.Delay(700, ct);
                    continue;
                }
                return new FiscalResult(rc == "SUCCESS", rc, node?["message"]?.GetValue<string>(), node?["data"]);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                return new FiscalResult(false, "NO_CONNECTION", ex.Message, null);
            }
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string Cut(string s, int maxBytes) => CutBytes(string.IsNullOrWhiteSpace(s) ? "Кассир" : s.Trim(), maxBytes);

    private static string CutBytes(string s, int maxBytes)
    {
        s = s.Trim();
        while (Encoding.UTF8.GetByteCount(s) > maxBytes) s = s[..^1];
        return s;
    }

    /// <summary>Понятное описание кода ошибки ККТ.</summary>
    public static string Describe(string code) => code switch
    {
        "SUCCESS" => "Выполнено",
        "NO_HOST" => "Не указан IP-адрес ККТ",
        "NO_CONNECTION" => "Нет связи с ККТ",
        "CORE_IS_NOT_ACTIVATED" => "Фискальный модуль не активирован",
        "CORE_IS_BLOCKED" => "Фискальный модуль заблокирован",
        "INVALID_DOC" => "Некорректный документ",
        "SHIFT_MUST_BE_CLOSED" => "Смена уже открыта",
        "SHIFT_MUST_BE_OPENED" => "Смена не открыта",
        "SHIFT_TOO_LONG" => "Смена открыта более 24 часов — закройте смену",
        "DATETIME_ERROR" => "Ошибка даты и времени ККТ",
        "KEYS_ERROR" => "Ошибка ключей",
        "FISCAL_MODULE_EXPIRED" => "Срок действия фискального модуля вышел",
        "FISCAL_MODULE_ERROR" => "Ошибка фискального модуля",
        "OUT_OF_PAPER" => "Закончилась бумага или открыта крышка",
        "CASH_NOT_ENOUGH" => "Недостаточно наличных в ККТ",
        "RECEIPT_SUM_TOO_HIGH" => "Сумма чека превышает лимит",
        "INVALID_PRODUCT_CODE" => "Ошибка кода товара",
        "DOCUMENT_SIZE_EXCEEDED" => "Чек слишком большой",
        "PRINTER_IS_BUSY" => "ККТ занята другим запросом",
        "BALANCE_IS_NEGATIVE" => "Недостаточно наличных для изъятия",
        "OUT_OF_DISK_SPACE_ERROR" => "На ККТ закончилось место",
        "FD_NOT_FOUND" => "Фискальный документ не найден",
        _ => code
    };
}
