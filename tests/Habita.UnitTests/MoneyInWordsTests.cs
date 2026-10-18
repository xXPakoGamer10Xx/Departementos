using Habita.Domain.Documents;
using Xunit;

namespace Habita.UnitTests;

public class MoneyInWordsTests
{
    [Theory]
    [InlineData(0, "CERO PESOS 00/100 M.N.")]
    [InlineData(1, "UN PESO 00/100 M.N.")]
    [InlineData(2, "DOS PESOS 00/100 M.N.")]
    [InlineData(10, "DIEZ PESOS 00/100 M.N.")]
    [InlineData(11, "ONCE PESOS 00/100 M.N.")]
    [InlineData(15, "QUINCE PESOS 00/100 M.N.")]
    [InlineData(16, "DIECISÉIS PESOS 00/100 M.N.")]
    [InlineData(20, "VEINTE PESOS 00/100 M.N.")]
    [InlineData(21, "VEINTIÚN PESOS 00/100 M.N.")]
    [InlineData(22, "VEINTIDÓS PESOS 00/100 M.N.")]
    [InlineData(23, "VEINTITRÉS PESOS 00/100 M.N.")]
    [InlineData(26, "VEINTISÉIS PESOS 00/100 M.N.")]
    [InlineData(29, "VEINTINUEVE PESOS 00/100 M.N.")]
    [InlineData(30, "TREINTA PESOS 00/100 M.N.")]
    [InlineData(31, "TREINTA Y UN PESOS 00/100 M.N.")]
    [InlineData(45, "CUARENTA Y CINCO PESOS 00/100 M.N.")]
    [InlineData(99, "NOVENTA Y NUEVE PESOS 00/100 M.N.")]
    [InlineData(100, "CIEN PESOS 00/100 M.N.")]
    [InlineData(101, "CIENTO UN PESOS 00/100 M.N.")]
    [InlineData(110, "CIENTO DIEZ PESOS 00/100 M.N.")]
    [InlineData(121, "CIENTO VEINTIÚN PESOS 00/100 M.N.")]
    [InlineData(200, "DOSCIENTOS PESOS 00/100 M.N.")]
    [InlineData(500, "QUINIENTOS PESOS 00/100 M.N.")]
    [InlineData(700, "SETECIENTOS PESOS 00/100 M.N.")]
    [InlineData(900, "NOVECIENTOS PESOS 00/100 M.N.")]
    [InlineData(999, "NOVECIENTOS NOVENTA Y NUEVE PESOS 00/100 M.N.")]
    [InlineData(1000, "MIL PESOS 00/100 M.N.")]
    [InlineData(1001, "MIL UN PESOS 00/100 M.N.")]
    [InlineData(1021, "MIL VEINTIÚN PESOS 00/100 M.N.")]
    [InlineData(1100, "MIL CIEN PESOS 00/100 M.N.")]
    [InlineData(1500, "MIL QUINIENTOS PESOS 00/100 M.N.")]
    [InlineData(2000, "DOS MIL PESOS 00/100 M.N.")]
    [InlineData(2021, "DOS MIL VEINTIÚN PESOS 00/100 M.N.")]
    [InlineData(3500, "TRES MIL QUINIENTOS PESOS 00/100 M.N.")]
    [InlineData(8500, "OCHO MIL QUINIENTOS PESOS 00/100 M.N.")]
    [InlineData(10000, "DIEZ MIL PESOS 00/100 M.N.")]
    [InlineData(11000, "ONCE MIL PESOS 00/100 M.N.")]
    [InlineData(21000, "VEINTIÚN MIL PESOS 00/100 M.N.")]
    [InlineData(21001, "VEINTIÚN MIL UN PESOS 00/100 M.N.")]
    [InlineData(31000, "TREINTA Y UN MIL PESOS 00/100 M.N.")]
    [InlineData(100000, "CIEN MIL PESOS 00/100 M.N.")]
    [InlineData(100001, "CIEN MIL UN PESOS 00/100 M.N.")]
    [InlineData(101000, "CIENTO UN MIL PESOS 00/100 M.N.")]
    [InlineData(999999, "NOVECIENTOS NOVENTA Y NUEVE MIL NOVECIENTOS NOVENTA Y NUEVE PESOS 00/100 M.N.")]
    [InlineData(1000000, "UN MILLÓN DE PESOS 00/100 M.N.")]
    [InlineData(1000001, "UN MILLÓN UN PESOS 00/100 M.N.")]
    [InlineData(1500000, "UN MILLÓN QUINIENTOS MIL PESOS 00/100 M.N.")]
    [InlineData(2000000, "DOS MILLONES DE PESOS 00/100 M.N.")]
    [InlineData(2500000, "DOS MILLONES QUINIENTOS MIL PESOS 00/100 M.N.")]
    [InlineData(21000000, "VEINTIÚN MILLONES DE PESOS 00/100 M.N.")]
    [InlineData(99999999, "NOVENTA Y NUEVE MILLONES NOVECIENTOS NOVENTA Y NUEVE MIL NOVECIENTOS NOVENTA Y NUEVE PESOS 00/100 M.N.")]
    [InlineData(100000000, "CIEN MILLONES DE PESOS 00/100 M.N.")]
    [InlineData(1000000000, "MIL MILLONES DE PESOS 00/100 M.N.")]
    public void Whole_amounts_are_spelled_the_way_contracts_spell_them(long amount, string expected) => Assert.Equal(expected, MoneyInWords.Mxn(amount));

    [Theory]
    [InlineData("0.5", "CERO PESOS 50/100 M.N.")]
    [InlineData("0.01", "CERO PESOS 01/100 M.N.")]
    [InlineData("1.99", "UN PESO 99/100 M.N.")]
    [InlineData("8500.5", "OCHO MIL QUINIENTOS PESOS 50/100 M.N.")]
    [InlineData("1234.56", "MIL DOSCIENTOS TREINTA Y CUATRO PESOS 56/100 M.N.")]
    [InlineData("1000000.50", "UN MILLÓN DE PESOS 50/100 M.N.")]
    [InlineData("0.999", "UN PESO 00/100 M.N.")]          // rounds up to a whole peso
    [InlineData("2.005", "DOS PESOS 01/100 M.N.")]         // halves round away from zero, like money
    [InlineData("1999.995", "DOS MIL PESOS 00/100 M.N.")]
    [InlineData("99999999.99", "NOVENTA Y NUEVE MILLONES NOVECIENTOS NOVENTA Y NUEVE MIL NOVECIENTOS NOVENTA Y NUEVE PESOS 99/100 M.N.")]
    public void Cents_are_two_digits_over_one_hundred_and_round_like_money(string amount, string expected) =>
        Assert.Equal(expected, MoneyInWords.Mxn(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void The_largest_supported_amount_is_spelled_out()
    {
        Assert.Equal("NOVECIENTOS NOVENTA Y NUEVE MIL NOVECIENTOS NOVENTA Y NUEVE MILLONES NOVECIENTOS NOVENTA Y NUEVE MIL NOVECIENTOS NOVENTA Y NUEVE PESOS 99/100 M.N.",
            MoneyInWords.Mxn(MoneyInWords.Max));
        Assert.Throws<ArgumentOutOfRangeException>(() => MoneyInWords.Mxn(MoneyInWords.Max + 0.01m));
        Assert.Throws<ArgumentOutOfRangeException>(() => MoneyInWords.Mxn(-1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => MoneyInWords.Mxn(-0.01m));
    }

    [Fact]
    public void Other_currencies_are_named_or_carry_their_code()
    {
        Assert.Equal("UN DÓLAR 00/100 USD", MoneyInWords.ForCurrency(1m, "usd"));
        Assert.Equal("DOS MIL QUINIENTOS DÓLARES 00/100 USD", MoneyInWords.ForCurrency(2500m, "USD"));
        Assert.Equal("QUINCE 00/100 EUR", MoneyInWords.ForCurrency(15m, "EUR"));
        Assert.Equal("OCHO MIL QUINIENTOS PESOS 00/100 M.N.", MoneyInWords.ForCurrency(8500m, "MXN"));
        Assert.Equal("CERO PESOS 00/100 M.N.", MoneyInWords.ForCurrency(0m, null));
    }

    // ---- the exhaustive part: an independent reader turns the words back into the number ----

    static readonly Dictionary<string, long> Words = new()
    {
        ["cero"] = 0, ["un"] = 1, ["uno"] = 1, ["dos"] = 2, ["tres"] = 3, ["cuatro"] = 4, ["cinco"] = 5, ["seis"] = 6, ["siete"] = 7, ["ocho"] = 8, ["nueve"] = 9,
        ["diez"] = 10, ["once"] = 11, ["doce"] = 12, ["trece"] = 13, ["catorce"] = 14, ["quince"] = 15, ["dieciséis"] = 16, ["diecisiete"] = 17, ["dieciocho"] = 18,
        ["diecinueve"] = 19, ["veinte"] = 20, ["veintiún"] = 21, ["veintidós"] = 22, ["veintitrés"] = 23, ["veinticuatro"] = 24, ["veinticinco"] = 25,
        ["veintiséis"] = 26, ["veintisiete"] = 27, ["veintiocho"] = 28, ["veintinueve"] = 29,
        ["treinta"] = 30, ["cuarenta"] = 40, ["cincuenta"] = 50, ["sesenta"] = 60, ["setenta"] = 70, ["ochenta"] = 80, ["noventa"] = 90,
        ["cien"] = 100, ["ciento"] = 100, ["doscientos"] = 200, ["trescientos"] = 300, ["cuatrocientos"] = 400, ["quinientos"] = 500, ["seiscientos"] = 600,
        ["setecientos"] = 700, ["ochocientos"] = 800, ["novecientos"] = 900,
    };

    /// <summary>Reads the Spanish words of a whole number back into the number. Written separately from the writer so that one cannot hide the other's mistakes.</summary>
    static long ReadBack(string words)
    {
        long total = 0, current = 0;
        foreach (var w in words.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (w is "y" or "de") continue;
            if (w == "mil") { current = (current == 0 ? 1 : current) * 1000; continue; }
            if (w is "millón" or "millones") { total += (current == 0 ? 1 : current) * 1_000_000; current = 0; continue; }
            Assert.True(Words.TryGetValue(w, out var v), $"unknown word «{w}» in «{words}»");
            current += v;
        }
        return total + current;
    }

    /// <summary>"TRES MIL PESOS 00/100 M.N." gives "TRES MIL"; "UN MILLÓN DE PESOS ..." gives "UN MILLÓN".</summary>
    static string WholeWords(string full)
    {
        var words = full[..full.IndexOf(" PESO", StringComparison.Ordinal)];
        return words.EndsWith(" DE", StringComparison.Ordinal) ? words[..^3] : words;
    }

    [Fact]
    public void The_first_two_hundred_thousand_integers_read_back_to_themselves_and_are_well_formed()
    {
        for (long n = 0; n <= 200_000; n++)
        {
            var text = MoneyInWords.Mxn(n);
            var words = WholeWords(text);
            Assert.Equal(n, ReadBack(words));
            Assert.Equal(text, text.ToUpperInvariant());
            Assert.DoesNotContain("  ", text);
            Assert.Equal(text.Trim(), text);
            Assert.DoesNotContain("UNO", text);                 // "uno" is always shortened before a noun
            Assert.EndsWith(n == 1 ? " PESO 00/100 M.N." : " PESOS 00/100 M.N.", text);
        }
    }

    [Fact]
    public void Large_and_boundary_amounts_read_back_to_themselves()
    {
        var rng = new Random(20261017);
        var samples = new List<long> { 999_999, 1_000_000, 1_000_001, 1_999_999, 2_000_000, 9_999_999, 10_000_000, 99_999_999, 100_000_000, 999_999_999, 1_000_000_000, 1_000_000_001,
                                       1_001_000_000, 21_000_000_000, 999_999_999_999 };
        for (var i = 0; i < 20_000; i++) samples.Add(rng.NextInt64(0, 1_000_000_000_000));
        for (var magnitude = 1L; magnitude <= 100_000_000_000; magnitude *= 10) { samples.Add(magnitude); samples.Add(magnitude - 1); samples.Add(magnitude + 1); samples.Add(magnitude * 21); samples.Add(magnitude * 31 + 1); }
        foreach (var n in samples.Where(n => n is >= 0 and <= 999_999_999_999))
        {
            var text = MoneyInWords.Mxn(n);
            Assert.Equal(n, ReadBack(WholeWords(text)));
            Assert.DoesNotContain("  ", text);
            Assert.DoesNotContain("UNO", text);
        }
    }

    [Fact]
    public void An_exact_number_of_millions_takes_de_and_nothing_else_does()
    {
        foreach (var n in new long[] { 1_000_000, 2_000_000, 21_000_000, 100_000_000, 1_000_000_000, 5_000_000_000 }) Assert.Contains(" DE PESOS", MoneyInWords.Mxn(n));
        foreach (var n in new long[] { 999_999, 1_000_001, 2_500_000, 1_000, 21_000 }) Assert.DoesNotContain(" DE PESOS", MoneyInWords.Mxn(n));
    }
}
