using System.Globalization;
using System.Text;

namespace Habita.Application.Reports;

/// <summary>Minimal RFC 4180 CSV writer with protection against spreadsheet formula injection.</summary>
public static class Csv
{
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>
    /// Text cells that a spreadsheet could execute as a formula (a tenant named "=HYPERLINK(...)") are prefixed with an apostrophe.
    /// Numbers and dates never go through this path, so legitimate negative amounts stay numeric.
    /// </summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var safe = value.IndexOfAny(FormulaStarts) == 0 ? "'" + value : value;
        return Quote(safe);
    }

    public static string Cell(object? value) => value switch
    {
        null => "",
        string s => Text(s),
        decimal d => d.ToString("F2", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        bool b => b ? "Sí" : "No",
        _ => Text(value.ToString()),
    };

    public static string Row(IEnumerable<object?> cells) => string.Join(',', cells.Select(Cell)) + "\r\n";

    private static string Quote(string s) =>
        s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>UTF-8 byte order mark so Excel opens accents correctly.</summary>
    public static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
}
