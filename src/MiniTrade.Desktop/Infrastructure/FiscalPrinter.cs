using Microsoft.Extensions.DependencyInjection;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Пробитие чеков продажи и возврата на ККТ Fiscat (если включено в Настройках → Кассовый аппарат).</summary>
public static class FiscalPrinter
{
    public static FiscalSettings Settings => App.Ui.Fiscal;
    public static bool Enabled => Settings.Enabled && !string.IsNullOrWhiteSpace(Settings.Host);
    public static FiscatClient Client => new(Settings);

    public static string Cashier
    {
        get
        {
            var u = App.Services.GetService<UserSession>()?.CurrentUser;
            return u is null ? "Кассир" : string.IsNullOrWhiteSpace(u.FullName) ? u.Login : u.FullName;
        }
    }

    /// <summary>Чек продажи. Возвращает текст ошибки или null (успех / ККТ выключена).</summary>
    public static async Task<string?> SaleAsync(Sale sale, IReadOnlyList<SalePayment> payments, decimal change,
        IEnumerable<CashRegister> registers)
    {
        if (!Enabled) return null;
        var cashless = registers.Where(r => r.IsCashless).Select(r => r.Id).ToHashSet();
        var nonCash = payments.Where(p => cashless.Contains(p.CashRegisterId)).Sum(p => p.Amount);
        var cash = payments.Where(p => !cashless.Contains(p.CashRegisterId)).Sum(p => p.Amount);
        var lines = sale.Items.Select(i => new FiscalLine(i.Product?.Name ?? L.T("Товар"), i.Price, i.Quantity, i.Amount,
            i.Product?.IsService == true, i.Product?.IsWeighted == true ? null : i.Product?.Barcode)).ToList();
        var r = await Client.ReceiptAsync(false, lines, cash, nonCash, change, Cashier);
        return r.Ok ? null : r.Error;
    }

    /// <summary>Чек возврата прихода по строкам продажи.</summary>
    public static async Task<string?> ReturnAsync(Sale sale, IReadOnlyList<ReturnLine> lines, CashRegister register)
    {
        if (!Enabled) return null;
        var items = sale.Items.ToDictionary(i => i.Id);
        var fl = lines.Where(l => items.ContainsKey(l.SaleItemId)).Select(l =>
        {
            var i = items[l.SaleItemId];
            var amount = Math.Round(i.Quantity == 0 ? 0 : i.Amount / i.Quantity * l.Quantity, 2);
            return new FiscalLine(i.Product?.Name ?? L.T("Товар"), i.Price, l.Quantity, amount, i.Product?.IsService == true,
                i.Product?.IsWeighted == true ? null : i.Product?.Barcode);
        }).ToList();
        var total = fl.Sum(l => l.Amount);
        var r = await Client.ReceiptAsync(true, fl, register.IsCashless ? 0 : total, register.IsCashless ? total : 0, 0, Cashier);
        return r.Ok ? null : r.Error;
    }
}
