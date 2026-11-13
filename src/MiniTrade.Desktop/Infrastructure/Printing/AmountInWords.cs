namespace MiniTrade.Desktop.Infrastructure.Printing;

/// <summary>Сумма прописью по-русски: «Одна тысяча двести сомони 50 дирам».</summary>
public static class AmountInWords
{
    private static readonly string[] UnitsM = ["", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять"];
    private static readonly string[] UnitsF = ["", "одна", "две", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять"];
    private static readonly string[] Teens = ["десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать",
        "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать"];
    private static readonly string[] Tens = ["", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто"];
    private static readonly string[] Hundreds = ["", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот"];

    // Разряды: (одна, две-четыре, пять и более, женский род)
    private static readonly (string One, string Few, string Many, bool Feminine)[] Scales =
    [
        ("", "", "", false),
        ("тысяча", "тысячи", "тысяч", true),
        ("миллион", "миллиона", "миллионов", false),
        ("миллиард", "миллиарда", "миллиардов", false)
    ];

    public static string Format(decimal amount, string currency = "сомони", string fraction = "дирам")
    {
        amount = Math.Round(Math.Abs(amount), 2);
        var whole = (long)Math.Truncate(amount);
        var cents = (int)((amount - whole) * 100);
        var words = Words(whole);
        return $"{char.ToUpper(words[0])}{words[1..]} {currency} {cents:00} {fraction}";
    }

    public static string Words(long n)
    {
        if (n == 0) return "ноль";
        var parts = new List<string>();
        for (var scale = Scales.Length - 1; scale >= 0; scale--)
        {
            var div = (long)Math.Pow(1000, scale);
            var group = (int)(n / div % 1000);
            if (group == 0) continue;
            parts.Add(Triple(group, Scales[scale].Feminine));
            if (scale > 0) parts.Add(Plural(group, Scales[scale].One, Scales[scale].Few, Scales[scale].Many));
        }
        return string.Join(" ", parts.Where(p => p.Length > 0));
    }

    private static string Triple(int n, bool feminine)
    {
        var parts = new List<string> { Hundreds[n / 100] };
        var rest = n % 100;
        if (rest is >= 10 and < 20) parts.Add(Teens[rest - 10]);
        else
        {
            parts.Add(Tens[rest / 10]);
            parts.Add((feminine ? UnitsF : UnitsM)[rest % 10]);
        }
        return string.Join(" ", parts.Where(p => p.Length > 0));
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var rest = n % 100;
        if (rest is >= 11 and <= 14) return many;
        return (rest % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }
}
