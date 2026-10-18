using Habita.Application.Reports;
using Xunit;

namespace Habita.UnitTests;

public class CsvTests
{
    [Theory]
    [InlineData("Ana Ejemplo", "Ana Ejemplo")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("Torre Norte, piso 2", "\"Torre Norte, piso 2\"")]            // comma → quoted
    [InlineData("dice \"hola\"", "\"dice \"\"hola\"\"\"")]                     // quotes doubled
    [InlineData("línea 1\nlínea 2", "\"línea 1\nlínea 2\"")]                   // newline → quoted
    public void Text_is_quoted_per_rfc4180(string? input, string expected) => Assert.Equal(expected, Csv.Text(input));

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")", "\"'=HYPERLINK(\"\"http://evil\"\",\"\"x\"\")\"")]
    [InlineData("+52 55 1234", "'+52 55 1234")]
    [InlineData("-cmd|' /C calc'!A0", "'-cmd|' /C calc'!A0")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\t=1", "'\t=1")]
    public void Formula_injection_is_neutralized_in_text_cells(string input, string expected) => Assert.Equal(expected, Csv.Text(input));

    [Fact]
    public void An_equals_sign_in_the_middle_is_harmless_and_left_alone() => Assert.Equal("a=b", Csv.Text("a=b"));

    [Fact]
    public void Numbers_dates_and_booleans_are_invariant_and_never_prefixed()
    {
        Assert.Equal("-100.50", Csv.Cell(-100.5m));          // a real negative amount stays numeric
        Assert.Equal("8500.00", Csv.Cell(8500m));
        Assert.Equal("1234.50", Csv.Cell(1234.5m));
        Assert.Equal("2026-10-05", Csv.Cell(new DateOnly(2026, 10, 5)));
        Assert.Equal("7", Csv.Cell(7));
        Assert.Equal("Sí", Csv.Cell(true));
        Assert.Equal("", Csv.Cell(null));
    }

    [Fact]
    public void Row_joins_cells_with_commas_and_ends_with_crlf() =>
        Assert.Equal("a,\"b,c\",2026-01-02,10.00\r\n", Csv.Row(["a", "b,c", new DateOnly(2026, 1, 2), 10m]));

    [Fact]
    public void Utf8_encoding_emits_a_byte_order_mark() => Assert.Equal([0xEF, 0xBB, 0xBF], Csv.Utf8WithBom.GetPreamble());
}
