using System.Text;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>
/// Страна для ввода телефона: код (+992) и маска национального номера ('#' — цифра).
/// Номер вводится только цифрами, разделители маски подставляются сами.
/// </summary>
public sealed class PhoneCountry
{
    private PhoneCountry(string iso, string name, string code, string mask)
    {
        Iso = iso;
        NameRu = name;
        Code = code;
        Mask = mask;
        Digits = mask.Count(c => c == '#');
    }

    public string Iso { get; }
    private string NameRu { get; }
    public string Name => L.T(NameRu);
    /// <summary>Телефонный код страны с «+», например «+992».</summary>
    public string Code { get; }
    public string Mask { get; }
    /// <summary>Сколько цифр в национальном номере (без кода страны).</summary>
    public int Digits { get; }
    /// <summary>Пример номера для подсказки в пустом поле.</summary>
    public string Placeholder => Mask.Replace('#', '0');

    public override string ToString() => $"{Name} {Code}";

    public static IReadOnlyList<PhoneCountry> All { get; } =
    [
        new("TJ", "Таджикистан", "+992", "## ### ## ##"),
        new("UZ", "Узбекистан", "+998", "## ### ## ##"),
        new("RU", "Россия", "+7", "### ###-##-##"),
        new("KZ", "Казахстан", "+7", "### ###-##-##"),
        new("KG", "Кыргызстан", "+996", "### ### ###"),
        new("TM", "Туркменистан", "+993", "## ##-##-##"),
        new("AF", "Афганистан", "+93", "## ### ####"),
        new("AZ", "Азербайджан", "+994", "## ### ## ##"),
        new("BY", "Беларусь", "+375", "## ###-##-##"),
        new("UA", "Украина", "+380", "## ### ## ##"),
        new("TR", "Турция", "+90", "### ### ## ##"),
        new("AE", "ОАЭ", "+971", "## ### ####"),
        new("IR", "Иран", "+98", "### ### ####"),
        new("CN", "Китай", "+86", "### #### ####"),
        new("US", "США", "+1", "(###) ###-####"),
    ];

    /// <summary>Страна по умолчанию — по языку интерфейса.</summary>
    public static PhoneCountry Default =>
        All.First(c => c.Iso == (L.Current.Code.StartsWith("uz") ? "UZ" : "TJ"));

    /// <summary>
    /// Цифры национального номера из любого ввода: вставленный целиком «+992 90 123 45 67»
    /// или «8 912 …» (Россия/Казахстан) — код страны и «8» отбрасываются, лишние цифры обрезаются.
    /// </summary>
    public string ExtractDigits(string? text)
    {
        var digits = new string((text ?? "").Where(char.IsAsciiDigit).ToArray());
        var code = Code.TrimStart('+');
        if (digits.Length > Digits && digits.StartsWith(code)) digits = digits[code.Length..];
        else if (code == "7" && digits.Length > Digits && digits.StartsWith('8')) digits = digits[1..];
        return digits.Length > Digits ? digits[..Digits] : digits;
    }

    /// <summary>Номер по маске; для неполного номера — только уже введённая часть (без хвостовых разделителей).</summary>
    public string Format(string digits)
    {
        var sb = new StringBuilder();
        var i = 0;
        foreach (var ch in Mask)
        {
            if (i >= digits.Length) break;
            if (ch == '#') sb.Append(digits[i++]);
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    public bool IsComplete(string? text) => ExtractDigits(text).Length == Digits;

    /// <summary>Полный международный номер: «+992 90 123 45 67».</summary>
    public string FullNumber(string? text) => $"{Code} {Format(ExtractDigits(text))}";
}
