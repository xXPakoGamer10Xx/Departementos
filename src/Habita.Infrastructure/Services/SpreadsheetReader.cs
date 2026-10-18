using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Habita.Application.Abstractions;
using Habita.Application.Imports;

namespace Habita.Infrastructure.Services;

/// <summary>
/// Reads the first sheet of an .xlsx (or the one called "Inquilinos") or a .csv into rows of cells. Excel dates and numbers arrive as such, so a date typed in Excel
/// is a date and not "46023". Only values are read: formulas are never evaluated and nothing is fetched. The header row is the first one that has recognisable headers.
/// </summary>
public sealed partial class SpreadsheetReader : ISpreadsheetReader
{
    private const int MaxRows = 2000, MaxColumns = 60, MaxCellChars = 2000, HeaderSearchRows = 15;

    public SpreadsheetResult Read(byte[] bytes)
    {
        if (bytes.Length == 0) return Fail("El archivo está vacío.");
        return bytes.Length >= 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K' ? ReadXlsx(bytes) : ReadCsv(bytes);
    }

    private static SpreadsheetResult Fail(string problem) => new(null, problem);

    private static SpreadsheetResult Build(string sheet, List<(int Row, List<SheetCell> Cells)> all)
    {
        var headerAt = all.Take(HeaderSearchRows).ToList().FindIndex(r => ImportParser.RecognizedCount(r.Cells.Select(c => c.Text)) >= 2);
        if (headerAt < 0) return Fail("No se encontraron los encabezados de las columnas (por ejemplo Nombre, Depto, Renta, Inicio). Revisa que estén en la primera fila de la hoja.");
        var headers = all[headerAt].Cells.Select(c => c.Text).ToList();
        var rows = all.Skip(headerAt + 1).Select(r => new SheetRow(r.Row, r.Cells)).ToList();
        return rows.Count > MaxRows ? Fail($"El archivo tiene más de {MaxRows} filas.") : new(new SpreadsheetData(sheet, headers, rows), null);
    }

    // ---------- csv ----------

    private SpreadsheetResult ReadCsv(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0) return Fail("El archivo no es un Excel (.xlsx) ni un CSV. Guárdalo como .xlsx o .csv.");
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); } // Excel's "CSV" is often Windows-1252, not UTF-8
        text = text.TrimStart('﻿');

        var delimiter = Delimiter(text);
        var lines = new List<(int Row, List<SheetCell> Cells)>();
        var cells = new List<SheetCell>(); var field = new StringBuilder(); var quoted = false; var row = 1; var rowStart = 1;
        void EndField() { var t = field.ToString(); cells.Add(new SheetCell(t.Length > MaxCellChars ? t[..MaxCellChars] : t)); field.Clear(); }
        void EndRow() { EndField(); if (cells.Count > 0 && cells.Any(c => c.Text.Length > 0)) lines.Add((rowStart, cells)); cells = []; rowStart = row + 1; }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else { field.Append(c); if (c == '\n') row++; }
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == delimiter) { EndField(); if (cells.Count > MaxColumns) return Fail($"El archivo tiene más de {MaxColumns} columnas."); }
            else if (c == '\n') { EndRow(); row++; }
            else if (c != '\r') field.Append(c);
            if (lines.Count > MaxRows + HeaderSearchRows + 5) return Fail($"El archivo tiene más de {MaxRows} filas.");
        }
        if (field.Length > 0 || cells.Count > 0) EndRow();
        return Build("CSV", lines);
    }

    /// <summary>Comma, semicolon or tab: whichever the header line uses most (a Mexican Excel often writes commas, a European one semicolons).</summary>
    private static char Delimiter(string text)
    {
        var sample = string.Join('\n', text.Split('\n').Take(HeaderSearchRows));
        var best = ','; var bestCount = -1;
        foreach (var d in new[] { ',', ';', '\t' })
        {
            var count = sample.Count(c => c == d);
            if (count > bestCount) { best = d; bestCount = count; }
        }
        return best;
    }

    // ---------- xlsx ----------

    private SpreadsheetResult ReadXlsx(byte[] bytes)
    {
        if (OpenXmlPackageGuard.Check(bytes, "xl/workbook.xml", "archivo de Excel (.xlsx)") is { } problem) return Fail(problem);
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var doc = SpreadsheetDocument.Open(ms, false);
            var workbook = doc.WorkbookPart;
            var sheets = workbook?.Workbook?.Sheets?.Elements<Sheet>().ToList();
            if (workbook is null || sheets is null || sheets.Count == 0) return Fail("El archivo de Excel no tiene hojas.");

            // The sheet called "Inquilinos" if there is one, else the first one that has the headers.
            var ordered = sheets.OrderBy(s => ImportParser.NormalizeHeader(s.Name?.Value) == "inquilinos" ? 0 : 1).ToList();
            SpreadsheetResult? last = null;
            foreach (var sheet in ordered)
            {
                if (workbook.GetPartById(sheet.Id!.Value!) is not WorksheetPart part) continue;
                var (rows, limit) = ReadSheet(workbook, part);
                if (limit is not null) return Fail(limit); // a limit, not a missing header: say so
                var built = Build(sheet.Name?.Value ?? "Hoja", rows!);
                if (built.Data is not null) return built;
                last ??= built;
            }
            return last ?? Fail("No se pudo leer ninguna hoja del archivo.");
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return Fail("No se pudo leer el archivo de Excel. Guárdalo de nuevo como .xlsx."); }
    }

    private static (List<(int Row, List<SheetCell> Cells)>? Data, string? Problem) ReadSheet(WorkbookPart workbook, WorksheetPart part)
    {
        var shared = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>()
            .Select(i => string.Concat(i.Elements<Text>().Select(t => t.Text).Concat(i.Elements<Run>().SelectMany(r => r.Elements<Text>()).Select(t => t.Text)))).ToList() ?? [];
        var dateStyles = DateStyles(workbook);
        var rows = new List<(int Row, List<SheetCell> Cells)>(); var lastIndex = 0;

        foreach (var row in part.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>() ?? [])
        {
            var rowNumber = row.RowIndex is { Value: > 0 } ri ? (int)ri.Value : lastIndex + 1; lastIndex = rowNumber;
            var cells = new List<SheetCell>();
            foreach (var cell in row.Elements<Cell>())
            {
                var col = ColumnIndex(cell.CellReference?.Value);
                if (col < 0 || col >= MaxColumns) continue;
                while (cells.Count <= col) cells.Add(default);
                cells[col] = ToCell(cell, shared, dateStyles);
            }
            if (cells.Any(c => c.Text.Length > 0)) rows.Add((rowNumber, cells));
            if (rows.Count > MaxRows + HeaderSearchRows + 5) return (null, $"El archivo tiene más de {MaxRows} filas.");
        }
        return (rows, null);
    }

    private static SheetCell ToCell(Cell cell, List<string> shared, HashSet<uint> dateStyles)
    {
        var raw = cell.CellValue?.Text ?? "";
        string Cap(string t) => t.Length > MaxCellChars ? t[..MaxCellChars] : t;
        switch (cell.DataType?.Value)
        {
            case var t when t == CellValues.SharedString: return int.TryParse(raw, out var i) && i >= 0 && i < shared.Count ? new SheetCell(Cap(shared[i])) : default;
            case var t when t == CellValues.InlineString: return new SheetCell(Cap(cell.InlineString?.InnerText ?? ""));
            case var t when t == CellValues.Boolean: return new SheetCell(raw == "1" ? "VERDADERO" : "FALSO");
            case var t when t == CellValues.Error: return default;
            case var t when t == CellValues.String: return new SheetCell(Cap(raw));
        }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return new SheetCell(Cap(raw));
        return new SheetCell(raw, number, IsDate: cell.StyleIndex?.Value is { } style && dateStyles.Contains(style));
    }

    /// <summary>Which cell styles show a number as a date: the built-in date formats and any custom format that uses day or year letters.</summary>
    private static HashSet<uint> DateStyles(WorkbookPart workbook)
    {
        var result = new HashSet<uint>();
        var stylesheet = workbook.WorkbookStylesPart?.Stylesheet;
        if (stylesheet?.CellFormats is null) return result;
        var custom = stylesheet.NumberingFormats?.Elements<NumberingFormat>().Where(n => n.NumberFormatId is not null)
            .ToDictionary(n => n.NumberFormatId!.Value, n => n.FormatCode?.Value ?? "") ?? [];
        uint index = 0;
        foreach (var format in stylesheet.CellFormats.Elements<CellFormat>())
        {
            var id = format.NumberFormatId?.Value ?? 0;
            if (id is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58) result.Add(index);
            else if (custom.TryGetValue(id, out var code) && LooksLikeDate(code)) result.Add(index);
            index++;
        }
        return result;
    }

    [GeneratedRegex("\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.")] private static partial Regex Literals();
    private static bool LooksLikeDate(string code) => Literals().Replace(code, "").IndexOfAny(['d', 'D', 'y', 'Y']) >= 0;

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return -1;
        var index = 0; var any = false;
        foreach (var c in reference)
        {
            if (!char.IsAsciiLetter(c)) break;
            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1); any = true;
            if (index > 20000) return -1;
        }
        return any ? index - 1 : -1;
    }
}
