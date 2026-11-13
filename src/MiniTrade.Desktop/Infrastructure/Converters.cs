using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MiniTrade.Core.Common;

using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Отображение суммы в сомони: 1 234,50 TJS.</summary>
public sealed class MoneyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal d ? Money.Format(d) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Ввод чисел: принимает и запятую, и точку как разделитель.</summary>
public sealed class DecimalConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal d ? d.ToString("0.####", culture) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string ?? "").Trim().Replace(" ", "").Replace('.', ',');
        if (s.Length == 0) return 0m;
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.GetCultureInfo("ru-RU"), out var d) ? d : Binding.DoNothing;
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value switch { bool x => x, null => false, string s => s.Length > 0, int n => n != 0, _ => true };
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Логическое «не» (например, IsEnabled = !IsBusy).</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Сравнение enum/значения с параметром для RadioButton.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string p && targetType.IsEnum ? Enum.Parse(targetType, p) : Binding.DoNothing;
}

/// <summary>Русские названия значений перечислений.</summary>
public sealed class EnumNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Core.Domain.StockMovementType t => t switch
        {
            Core.Domain.StockMovementType.Purchase => L.T("Закупка"),
            Core.Domain.StockMovementType.Sale => L.T("Продажа"),
            Core.Domain.StockMovementType.SaleReturn => L.T("Возврат"),
            Core.Domain.StockMovementType.TransferOut => L.T("Перемещение (−)"),
            Core.Domain.StockMovementType.TransferIn => L.T("Перемещение (+)"),
            Core.Domain.StockMovementType.InventoryAdjust => L.T("Инвентаризация"),
            _ => t.ToString()
        },
        Core.Domain.UserRole r => r switch
        {
            Core.Domain.UserRole.Admin => L.T("Администратор"),
            Core.Domain.UserRole.Manager => L.T("Менеджер"),
            _ => L.T("Кассир")
        },
        Core.Domain.AuditAction a => AuditLogViewModel.ActionName(a),
        Core.Domain.CashDirection d => d == Core.Domain.CashDirection.Income ? L.T("Доход") : L.T("Расход"),
        bool b => b ? L.T("Да") : L.T("Нет"),
        _ => value?.ToString() ?? ""
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>[доля 0..1, ширина дорожки] → ширина заполненной части (полоски на главной).</summary>
public static class Ratio
{
    public static readonly IMultiValueConverter Width = new WidthConverter();

    private sealed class WidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values is [double share, double width] ? Math.Max(0, Math.Min(1, share)) * width : 0d;

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
