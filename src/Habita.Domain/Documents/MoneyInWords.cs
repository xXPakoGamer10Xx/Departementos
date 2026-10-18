using System.Globalization;

namespace Habita.Domain.Documents;

/// <summary>
/// An amount of money written out the way contracts and receipts write it: "TRES MIL QUINIENTOS PESOS 00/100 M.N.".
/// The cents are always two digits over 100. Whole numbers are spelled in Spanish with the usual rules: "cien" but "ciento uno", "un" (not "uno")
/// before a noun ("veintiún mil", "treinta y un pesos"), "mil" (not "un mil"), "un millón de pesos" when nothing follows the millions.
/// </summary>
public static class MoneyInWords
{
    public const decimal Max = 999_999_999_999.99m;

    private static readonly string[] Units =
    [
        "cero", "uno", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve", "diez", "once", "doce", "trece", "catorce", "quince",
        "dieciséis", "diecisiete", "dieciocho", "diecinueve", "veinte", "veintiuno", "veintidós", "veintitrés", "veinticuatro", "veinticinco",
        "veintiséis", "veintisiete", "veintiocho", "veintinueve",
    ];
    private static readonly string[] Tens = ["", "", "", "treinta", "cuarenta", "cincuenta", "sesenta", "setenta", "ochenta", "noventa"];
    private static readonly string[] Hundreds = ["", "ciento", "doscientos", "trescientos", "cuatrocientos", "quinientos", "seiscientos", "setecientos", "ochocientos", "novecientos"];

    /// <summary>Mexican pesos: "OCHO MIL QUINIENTOS PESOS 50/100 M.N.".</summary>
    public static string Mxn(decimal amount) => Format(amount, "peso", "pesos", "M.N.");

    /// <summary>Any currency. MXN and USD get their proper names; any other code is written after the cents ("... 00/100 EUR").</summary>
    public static string ForCurrency(decimal amount, string? currencyCode) => (currencyCode ?? "MXN").ToUpperInvariant() switch
    {
        "MXN" => Mxn(amount),
        "USD" => Format(amount, "dólar", "dólares", "USD"),
        var other => Format(amount, "", "", other),
    };

    public static string Format(decimal amount, string singular, string plural, string suffix)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "El importe no puede ser negativo.");
        var rounded = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (rounded > Max) throw new ArgumentOutOfRangeException(nameof(amount), "El importe es demasiado grande para escribirlo con letra.");
        var whole = (long)decimal.Truncate(rounded);
        var cents = (int)((rounded - whole) * 100);

        var words = Number(whole);
        var noun = whole == 1 ? singular : plural;
        // "un millón DE pesos", "dos millones DE pesos": the "de" appears only when the amount is an exact number of millions.
        var de = noun.Length > 0 && whole >= 1_000_000 && whole % 1_000_000 == 0 ? " de" : "";
        var text = noun.Length > 0 ? $"{words}{de} {noun} {cents:D2}/100 {suffix}" : $"{words} {cents:D2}/100 {suffix}";
        return text.ToUpper(new CultureInfo("es-MX"));
    }

    /// <summary>A whole number in words, with "uno" shortened to "un" (it always stands before a noun: "un peso", "veintiún mil", "treinta y un millones").</summary>
    public static string Number(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (n == 0) return "cero";
        if (n >= 1_000_000)
        {
            var millions = n / 1_000_000; var rest = n % 1_000_000;
            var head = millions == 1 ? "un millón" : $"{Number(millions)} millones";
            return rest == 0 ? head : $"{head} {Number(rest)}";
        }
        if (n >= 1000)
        {
            var thousands = n / 1000; var rest = n % 1000;
            var head = thousands == 1 ? "mil" : $"{Number(thousands)} mil";
            return rest == 0 ? head : $"{head} {Below1000(rest)}";
        }
        return Below1000(n);
    }

    private static string Below1000(long n)
    {
        if (n == 100) return "cien";
        var hundreds = (int)(n / 100); var rest = (int)(n % 100);
        var words = new List<string>(2);
        if (hundreds > 0) words.Add(Hundreds[hundreds]);
        if (rest > 0) words.Add(Below100(rest));
        return string.Join(' ', words);
    }

    private static string Below100(int n)
    {
        if (n < 30) return n switch { 1 => "un", 21 => "veintiún", _ => Units[n] };
        var tens = n / 10; var units = n % 10;
        return units == 0 ? Tens[tens] : $"{Tens[tens]} y {(units == 1 ? "un" : Units[units])}";
    }
}
