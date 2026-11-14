namespace MiniTrade.Core.Common;

/// <summary>Страна: код ISO, название, валюта учёта, телефонный код.</summary>
public sealed record CountryInfo(string Code, string Name, string Currency, string PhoneCode)
{
    public override string ToString() => Name;
}

/// <summary>Страны для настроек компании: выбор страны задаёт валюту и телефонный код (подставляется в телефон нового контрагента).</summary>
public static class Countries
{
    public const string DefaultCode = "TJ";

    public static readonly IReadOnlyList<CountryInfo> All =
    [
        new("TJ", "Таджикистан", "TJS", "+992"),
        new("UZ", "Узбекистан", "UZS", "+998"),
        new("KZ", "Казахстан", "KZT", "+7"),
        new("KG", "Кыргызстан", "KGS", "+996"),
        new("TM", "Туркменистан", "TMT", "+993"),
        new("RU", "Россия", "RUB", "+7"),
        new("AZ", "Азербайджан", "AZN", "+994"),
        new("AM", "Армения", "AMD", "+374"),
        new("BY", "Беларусь", "BYN", "+375"),
        new("MD", "Молдова", "MDL", "+373"),
        new("GE", "Грузия", "GEL", "+995"),
        new("UA", "Украина", "UAH", "+380"),
        new("AF", "Афганистан", "AFN", "+93"),
        new("TR", "Турция", "TRY", "+90"),
        new("AE", "ОАЭ", "AED", "+971"),
        new("CN", "Китай", "CNY", "+86"),
        new("US", "США", "USD", "+1"),
    ];

    public static CountryInfo? Find(string? code) => All.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Телефонный код страны компании (задаётся после входа и при сохранении настроек).</summary>
    public static string PhoneCode { get; private set; } = "+992";

    public static void SetCurrent(string? code) => PhoneCode = Find(code)?.PhoneCode ?? "";
}
