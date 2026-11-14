namespace MiniTrade.Core.Common;

/// <summary>Правила округления денег и количеств. Валюта — сомони (TJS), 1 TJS = 100 дирам.</summary>
public static class Money
{
    public const string DefaultCurrency = "TJS";

    /// <summary>Валюта учёта из настроек компании (по умолчанию TJS). Задаётся после входа и при сохранении настроек.</summary>
    public static string Currency { get; private set; } = DefaultCurrency;

    /// <summary>Нормализует код валюты (пусто → TJS) и делает его текущим.</summary>
    public static string SetCurrency(string? code)
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        return Currency = c.Length == 0 ? DefaultCurrency : c;
    }

    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal RoundQty(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    /// <summary>Себестоимость единицы храним с повышенной точностью.</summary>
    public static decimal RoundCost(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    public static string Format(decimal value) => $"{value:N2} {Currency}";
}

/// <summary>Ошибка бизнес-правила — сообщение показывается пользователю как есть.</summary>
/// <summary>
/// Ошибка бизнес-правила с текстом для пользователя (на русском).
/// Для сообщений с подстановкой значений хранит формат и аргументы — интерфейс переводит формат и подставляет их сам.
/// </summary>
public class BusinessException : Exception
{
    public BusinessException(string message) : base(message) { }

    public BusinessException(string format, params object?[] args)
        : base(string.Format(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), format, args))
    {
        Format = format;
        Args = args;
    }

    /// <summary>Строка формата (ключ перевода), если сообщение собрано из значений.</summary>
    public string? Format { get; }
    public object?[] Args { get; } = [];
}
