using Habita.Application.Abstractions;
using Habita.Application.Imports;
using Xunit;

namespace Habita.UnitTests;

public class ImportParserTests
{
    [Theory]
    [InlineData("3500", 3500)] [InlineData("$3,500.00", 3500)] [InlineData("3,500", 3500)] [InlineData(" $ 8 500 ", 8500)] [InlineData("3500,50", 3500.5)] [InlineData("1,234,567.89", 1234567.89)]
    public void Money_is_read_in_the_usual_spellings(string text, double expected) => Assert.Equal((decimal)expected, ImportParser.ParseMoney(new SheetCell(text)).Value);

    [Theory] [InlineData("abc")] [InlineData("-5")] [InlineData("100000000")] [InlineData("1.234")]
    public void Money_that_is_not_an_amount_is_refused(string text) => Assert.NotNull(ImportParser.ParseMoney(new SheetCell(text)).Problem);

    [Fact] public void Excel_floating_noise_is_rounded_but_real_extra_decimals_are_not() { Assert.Equal(0.3m, ImportParser.ParseMoney(new SheetCell("0.3", 0.30000000000000004)).Value); Assert.Null(ImportParser.ParseMoney(new SheetCell("", 10.123)).Value); }

    [Theory]
    [InlineData("05/01/2026", 2026, 1, 5)] [InlineData("2026-01-05", 2026, 1, 5)] [InlineData("5 de enero de 2026", 2026, 1, 5)] [InlineData("5/1/26", 2026, 1, 5)]
    public void Dates_are_day_first(string text, int y, int m, int d) => Assert.Equal(new DateOnly(y, m, d), ImportParser.ParseDate(new SheetCell(text)).Value);

    [Fact] public void An_excel_date_serial_is_a_date() => Assert.Equal(new DateOnly(2026, 7, 1), ImportParser.ParseDate(new SheetCell("", new DateTime(2026, 7, 1).ToOADate(), IsDate: true)).Value);
    [Theory] [InlineData("ayer")] [InlineData("32/01/2026")] [InlineData("01/01/1950")] public void Bad_dates_are_refused(string text) => Assert.NotNull(ImportParser.ParseDate(new SheetCell(text)).Problem);

    [Theory] [InlineData("5", 5)] [InlineData("05 de cada mes", 5)] [InlineData("los días 10", 10)] [InlineData("28", 28)]
    public void Due_day_is_found_in_the_text(string text, int day) => Assert.Equal(day, ImportParser.ParseDueDay(new SheetCell(text)).Day);
    [Theory] [InlineData("29")] [InlineData("31")] [InlineData("0")] [InlineData("sin fecha")] public void Due_days_Habita_cannot_use_are_refused(string text) => Assert.NotNull(ImportParser.ParseDueDay(new SheetCell(text)).Problem);

    [Fact] public void Headers_match_by_meaning_not_spelling()
    {
        var (map, unknown) = ImportParser.MapHeaders(["  NOMBRE ", "Número de Depto", "Renta Mensual", "Fecha de Término", "Teléfonos", "Color favorito"]);
        Assert.Equal(0, map[ImportColumn.Name]); Assert.Equal(1, map[ImportColumn.Unit]); Assert.Equal(2, map[ImportColumn.Rent]); Assert.Equal(3, map[ImportColumn.End]); Assert.Equal(4, map[ImportColumn.Phone]);
        Assert.Equal(["Color favorito"], unknown);
    }

    [Fact] public void Several_phones_keep_the_first_and_do_not_lose_the_rest()
    {
        var (phone, others, invalid) = ImportParser.SplitPhones("55 1234 5678 / 55 8765 4321 y xx");
        Assert.Equal("55 1234 5678", phone); Assert.Equal(["55 8765 4321"], others); Assert.Equal(["xx"], invalid);
    }

    [Fact] public void The_same_name_in_different_spelling_has_one_key() => Assert.Equal(ImportParser.NameKey("José  PÉREZ"), ImportParser.NameKey(" jose perez "));
    [Fact] public void Unit_numbers_from_excel_have_no_decimal_point() => Assert.Equal("101", ImportParser.Clean(new SheetCell("101", 101.0)));
}
