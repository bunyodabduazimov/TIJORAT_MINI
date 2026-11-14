using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

public sealed record FiscalOption(string Value, string Name);

/// <summary>Настройки → Кассовый аппарат: ККТ Fiscat этого компьютера (хранится в ui.json, не в общей базе).</summary>
public partial class SettingsViewModel
{
    public IReadOnlyList<FiscalOption> FiscalTaxTypes { get; } =
    [
        new("GENERAL", L.T("Общий режим")),
        new("SIMPLIFIED1", L.T("УСН 6%")),
        new("SIMPLIFIED2", L.T("УСН 13%")),
        new("SIMPLIFIED3", L.T("УСН 18%")),
        new("SPECIAL", L.T("Специальный режим")),
        new("SIMPLIFIED4", L.T("УСН (−50%) 3%")),
        new("SIMPLIFIED5", L.T("УСН (−50%) 6,5%")),
        new("SIMPLIFIED6", L.T("УСН (−50%) 9%"))
    ];

    public IReadOnlyList<FiscalOption> FiscalVatCodes { get; } =
    [
        new("STANDARD", L.T("НДС 14%")),
        new("REDUCED1", L.T("НДС 7%")),
        new("REDUCED2", L.T("НДС 5%")),
        new("REDUCED4", L.T("НДС 10%")),
        new("REDUCED3", L.T("НДС 2,5%")),
        new("ZERO_TAX", L.T("Без НДС (освобождение)")),
        new("ZERO_TAX_EXPORT", L.T("НДС 0% (экспорт)"))
    ];

    private static FiscalSettings F => App.Ui.Fiscal;

    [ObservableProperty] private bool _fiscalEnabled = F.Enabled;
    [ObservableProperty] private string _fiscalHost = F.Host;
    [ObservableProperty] private string _fiscalPort = F.Port.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string _fiscalTaxType = F.TaxType;
    [ObservableProperty] private string _fiscalVatCode = F.VatCode;
    [ObservableProperty] private bool _fiscalPrintSlip = F.PrintSlip;
    [ObservableProperty] private bool _fiscalOpenDrawer = F.OpenDrawer;
    [ObservableProperty] private string? _fiscalStatus;
    [ObservableProperty] private bool _fiscalBusy;

    [RelayCommand]
    private void SaveFiscal()
    {
        if (!ApplyFiscal()) return;
        App.Ui.Save();
        Dialogs.Info(FiscalEnabled ? L.T("Настройки ККТ сохранены. Чеки продаж будут пробиваться на ККТ.")
                                   : L.T("Настройки ККТ сохранены. ККТ отключена."));
    }

    private bool ApplyFiscal()
    {
        var host = (FiscalHost ?? "").Trim();
        if (FiscalEnabled && host.Length == 0) { Dialogs.Error(L.T("Укажите IP-адрес ККТ.")); return false; }
        if (!int.TryParse(FiscalPort, out var port) || port is < 1 or > 65535) { Dialogs.Error(L.T("Порт ККТ — число от 1 до 65535.")); return false; }
        F.Enabled = FiscalEnabled;
        F.Host = host;
        F.Port = port;
        F.TaxType = FiscalTaxType;
        F.VatCode = FiscalVatCode;
        F.PrintSlip = FiscalPrintSlip;
        F.OpenDrawer = FiscalOpenDrawer;
        return true;
    }

    private async Task FiscalCallAsync(Func<FiscatClient, Task<FiscalResult>> call, string done)
    {
        if (!ApplyFiscal()) return;
        FiscalBusy = true;
        try
        {
            var r = await call(new FiscatClient(F));
            if (r.Ok) Dialogs.Info(done + (r.FdNumberOrNull() is { } fd ? "\n" + L.F("Номер ФД: {0}", fd) : ""));
            else Dialogs.Error(r.Error);
        }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
        finally { FiscalBusy = false; }
    }

    [RelayCommand]
    private async Task FiscalCheckAsync()
    {
        if (!ApplyFiscal()) return;
        FiscalBusy = true;
        try
        {
            var r = await new FiscatClient(F).StatusAsync();
            if (!r.Ok) { FiscalStatus = r.Error; return; }
            var d = r.Data;
            string S(string k) => d?[k]?.ToString() ?? "—";
            bool B(string k) => d?[k]?.GetValue<bool>() == true;
            FiscalStatus = string.Join("\n", new[]
            {
                L.F("Модель: {0}, № {1}", S("model"), S("serialNumber")),
                L.F("Касса: {0}", S("terminalName")),
                B("blocked") ? L.F("ЗАБЛОКИРОВАНО: {0}", S("reasonForBlocking")) : L.T("Не заблокирована"),
                B("registered") ? L.T("Зарегистрирована") : L.T("Не зарегистрирована"),
                B("shiftStatus") ? L.F("Смена № {0} открыта", S("shiftNumber")) : L.T("Смена закрыта"),
                L.F("Связь с ОФД: {0}, неотправленных ФД: {1}", B("onlineStatus") ? L.T("есть") : L.T("нет"), S("queueCount")),
                B("isPaperPresent") ? L.T("Бумага есть") : L.T("Нет бумаги"),
                L.F("Срок ФН: {0} дн.", S("fnLifetimeInDays"))
            });
        }
        catch (Exception ex) { FiscalStatus = ex.Message; }
        finally { FiscalBusy = false; }
    }

    [RelayCommand] private Task FiscalOpenShiftAsync() => FiscalCallAsync(c => c.OpenShiftAsync(FiscalPrinter.Cashier), L.T("Смена открыта."));

    [RelayCommand]
    private Task FiscalCloseShiftAsync() =>
        Dialogs.Confirm(L.T("Закрыть смену на ККТ и напечатать Z-отчёт?"))
            ? FiscalCallAsync(c => c.CloseShiftAsync(FiscalPrinter.Cashier), L.T("Смена закрыта, Z-отчёт напечатан."))
            : Task.CompletedTask;

    [RelayCommand] private Task FiscalXReportAsync() => FiscalCallAsync(c => c.XReportAsync(), L.T("X-отчёт напечатан."));
    [RelayCommand] private Task FiscalPrintLastAsync() => FiscalCallAsync(c => c.PrintLastAsync(), L.T("Последний документ напечатан."));
    [RelayCommand] private Task FiscalOpenDrawerAsync() => FiscalCallAsync(c => c.OpenDrawerAsync(), L.T("Денежный ящик открыт."));

    [RelayCommand] private Task FiscalAddCashAsync() => FiscalCashAsync(true);
    [RelayCommand] private Task FiscalRemoveCashAsync() => FiscalCashAsync(false);

    private Task FiscalCashAsync(bool add)
    {
        var input = Dialogs.Prompt(add ? L.T("Сумма внесения наличных в ККТ:") : L.T("Сумма изъятия наличных из ККТ:"),
            add ? L.T("Внесение наличных") : L.T("Изъятие наличных"));
        if (input is null) return Task.CompletedTask;
        if (!decimal.TryParse(input.Replace('.', ','), NumberStyles.Number, new CultureInfo("ru-RU"), out var amount) || amount <= 0)
        {
            Dialogs.Error(L.T("Введите сумму больше нуля."));
            return Task.CompletedTask;
        }
        return FiscalCallAsync(c => add ? c.AddCashAsync(amount) : c.RemoveCashAsync(amount),
            add ? L.T("Наличные внесены.") : L.T("Наличные изъяты."));
    }
}

internal static class FiscalResultExtensions
{
    public static long? FdNumberOrNull(this FiscalResult r)
    {
        try { return r.Data?["fdNumber"]?.GetValue<long>(); } catch { return null; }
    }
}
