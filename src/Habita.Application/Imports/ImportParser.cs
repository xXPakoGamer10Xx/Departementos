using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Habita.Application.Abstractions;
using Habita.Domain.Notifications;
using Habita.Domain.Properties;
using Habita.Domain.Tenants;

namespace Habita.Application.Imports;

public enum ImportColumn { Name, Unit, Rent, RentWords, DueDay, Start, End, Guarantor, GuarantorPhone, Phone, Email, Notes, Deposit }

/// <summary>What one spreadsheet row says, read but not yet checked against the database.</summary>
public class ParsedRow
{
    public int Row { get; init; }
    public string Name { get; set; } = "";
    public string UnitCode { get; set; } = "";
    public decimal? Rent { get; set; }
    public int? DueDay { get; set; }
    public DateOnly? Start { get; set; }
    public DateOnly? End { get; set; }
    public string? Guarantor { get; set; }
    public string? GuarantorPhone { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Notes { get; set; }
    public decimal? Deposit { get; set; }
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Turns the landlord's own spreadsheet into rows. Headers are matched by meaning, not by exact spelling ("Término", "termino", "Fecha de término" are one column),
/// numbers may be written "$3,500.00", dates as Excel dates or as text, and the day of payment as "5" or "05 de cada mes". Nothing here touches the database.
/// </summary>
public static partial class ImportParser
{
    public const int MaxRows = 500;
    public static readonly ImportColumn[] Required = [ImportColumn.Name, ImportColumn.Unit, ImportColumn.Rent, ImportColumn.Start];

    private static readonly (ImportColumn Column, string Label, string[] Names)[] Headers =
    [
        (ImportColumn.Name, "Nombre", ["nombre", "nombrecompleto", "inquilino", "nombredelinquilino", "arrendatario", "nombredelarrendatario"]),
        (ImportColumn.Unit, "Depto", ["depto", "departamento", "unidad", "numerodedepto", "numerodedepartamento", "deptonumero", "numerodeunidad", "local", "casa", "habitacion"]),
        (ImportColumn.Rent, "Renta", ["renta", "rentamensual", "mensualidad", "importe", "precio", "montoderenta"]),
        (ImportColumn.RentWords, "Con letra", ["conletra", "rentaletra", "rentaconletra", "importeconletra", "rentacletra"]),
        (ImportColumn.DueDay, "Fecha de pago", ["fechadepago", "diadepago", "diadepagomensual", "fechapago", "diadevencimiento", "pago", "diapago"]),
        (ImportColumn.Start, "Inicio", ["inicio", "fechadeinicio", "fechainicio", "iniciodecontrato", "iniciodelcontrato", "desde"]),
        (ImportColumn.End, "Término", ["termino", "fechadetermino", "fechatermino", "fin", "fechafin", "findecontrato", "findelcontrato", "hasta", "vencimientodecontrato"]),
        (ImportColumn.Guarantor, "Fiador", ["fiador", "nombredelfiador", "aval", "fiadornombre"]),
        (ImportColumn.GuarantorPhone, "Teléfono del fiador", ["telefonofiador", "telefonodelfiador", "telfiador", "telefonoaval", "fiadortelefono", "celularfiador"]),
        (ImportColumn.Phone, "Teléfonos", ["telefonos", "telefono", "tel", "celular", "contacto", "telefonodelinquilino", "teldeinquilino"]),
        (ImportColumn.Email, "Correo", ["correo", "email", "correoelectronico", "mail", "emaildelinquilino"]),
        (ImportColumn.Notes, "Observaciones", ["observaciones", "notas", "comentarios", "observacion", "nota"]),
        (ImportColumn.Deposit, "Depósito", ["deposito", "compensacion", "garantia", "depositogarantia"]),
    ];

    public static string Label(ImportColumn c) => Headers.First(h => h.Column == c).Label;

    // ---------- normalizing ----------

    /// <summary>Lower case, no accents, no spaces or punctuation: "Fecha de Término" → "fechadetermino".</summary>
    public static string NormalizeHeader(string? text)
    {
        var sb = new StringBuilder();
        foreach (var c in (text ?? "").Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>The key two spellings of one person's name share: "  JOSÉ  Pérez " and "jose perez" are the same tenant.</summary>
    public static string NameKey(string? name)
    {
        var sb = new StringBuilder();
        foreach (var c in PersonName.Display(name).Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.IsWhiteSpace(c) ? ' ' : char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static (Dictionary<ImportColumn, int> Map, List<string> Unknown) MapHeaders(IReadOnlyList<string> headers)
    {
        var map = new Dictionary<ImportColumn, int>(); var unknown = new List<string>();
        for (var i = 0; i < headers.Count; i++)
        {
            var key = NormalizeHeader(headers[i]);
            if (key.Length == 0) continue;
            var hit = Headers.Where(h => h.Names.Contains(key)).Select(h => (ImportColumn?)h.Column).FirstOrDefault();
            if (hit is { } column) map.TryAdd(column, i); else unknown.Add(headers[i].Trim());
        }
        return (map, unknown);
    }

    /// <summary>How many headers of this row mean something to us. The header row is the first row with at least two.</summary>
    public static int RecognizedCount(IEnumerable<string> cells) => cells.Select(NormalizeHeader).Count(k => k.Length > 0 && Headers.Any(h => h.Names.Contains(k)));

    // ---------- values ----------

    [GeneratedRegex(@"^\$?\s*\d{1,3}(,\d{3})+(\.\d+)?$")] private static partial Regex ThousandsPattern();

    /// <summary>"3500", "3,500.00", "$3,500", "3500.5" and Excel numbers. Null with a reason when it is not an amount.</summary>
    public static (decimal? Value, string? Problem) ParseMoney(SheetCell cell)
    {
        if (cell.Number is { } n && !cell.IsDate) return Check((decimal)n);
        var text = cell.Text.Trim();
        if (text.Length == 0) return (null, null);
        var cleaned = text.Replace("$", "").Replace("MXN", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "").Replace(" ", "");
        if (ThousandsPattern().IsMatch(cleaned) || (cleaned.Contains(',') && !cleaned.Contains('.') && Regex.IsMatch(cleaned, @"^\d{1,3}(,\d{3})+$"))) cleaned = cleaned.Replace(",", "");
        else if (Regex.IsMatch(cleaned, @"^\d+,\d{1,2}$")) cleaned = cleaned.Replace(',', '.'); // "3500,50"
        return decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? Check(v) : (null, $"«{text}» no es un importe.");

        static (decimal?, string?) Check(decimal v)
        {
            if (v < 0) return (null, "El importe no puede ser negativo.");
            if (v > Unit.MaxRent) return (null, "El importe no puede superar 99,999,999.99.");
            var rounded = decimal.Round(v, 2);
            return Math.Abs(v - rounded) < 0.0000001m ? (rounded, null) : (null, "El importe admite máximo 2 decimales."); // 0.30000000000000004 from Excel is 0.30
        }
    }

    private static readonly string[] DateFormats =
    [
        "d/M/yyyy", "dd/MM/yyyy", "d/M/yy", "dd/MM/yy", "yyyy-MM-dd", "d-M-yyyy", "dd-MM-yyyy", "d.M.yyyy", "yyyy/MM/dd",
        "d 'de' MMMM 'de' yyyy", "dd 'de' MMMM 'de' yyyy", "d MMMM yyyy", "d-MMM-yyyy", "d/MMM/yyyy", "MMMM d, yyyy",
    ];
    private static readonly CultureInfo Spanish = new("es-MX");

    /// <summary>An Excel date, or text in the usual Mexican forms (day first). Null with a reason when it is neither.</summary>
    public static (DateOnly? Value, string? Problem) ParseDate(SheetCell cell)
    {
        if (cell.Number is { } serial && (cell.IsDate || serial is > 20000 and < 80000))
        {
            try { var d = DateOnly.FromDateTime(DateTime.FromOADate(serial)); return d.Year is >= 2000 and <= 2100 ? (d, null) : (null, $"La fecha {d:dd/MM/yyyy} está fuera de rango."); }
            catch (ArgumentException) { return (null, "La fecha no es válida."); }
        }
        var text = cell.Text.Trim();
        if (text.Length == 0) return (null, null);
        foreach (var culture in new[] { Spanish, CultureInfo.InvariantCulture })
            if (DateTime.TryParseExact(text, DateFormats, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
                return parsed.Year is >= 2000 and <= 2100 ? (DateOnly.FromDateTime(parsed), null) : (null, $"La fecha «{text}» está fuera de rango.");
        return (null, $"«{text}» no es una fecha (usa día/mes/año, por ejemplo 05/01/2026).");
    }

    [GeneratedRegex(@"(?<!\d)(\d{1,2})(?!\d)")] private static partial Regex DayPattern();

    /// <summary>"5", "05 de cada mes", "los días 5" or a date (its day). Null when empty; an error when the day cannot be used.</summary>
    public static (int? Day, string? Problem) ParseDueDay(SheetCell cell)
    {
        if (cell.IsDate && cell.Number is { } serial) { try { return Limit(DateTime.FromOADate(serial).Day); } catch (ArgumentException) { return (null, "La fecha de pago no es válida."); } }
        if (cell.Number is { } n) return n == Math.Floor(n) ? Limit((int)n) : (null, $"«{cell.Text}» no es un día del mes.");
        var text = cell.Text.Trim();
        if (text.Length == 0) return (null, null);
        var match = DayPattern().Match(text);
        return match.Success ? Limit(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)) : (null, $"No se encontró un día del mes en «{text}».");

        static (int?, string?) Limit(int d) => d is >= 1 and <= 28 ? (d, null)
            : d is > 28 and <= 31 ? (null, $"El día de pago {d} no se admite: Habita usa del 1 al 28 para que exista en todos los meses.") : (null, $"{d} no es un día del mes.");
    }

    [GeneratedRegex(@"[/;,\n]| y ")] private static partial Regex PhoneSeparators();

    /// <summary>Splits "55 1234 5678 / 55 8765 4321". The first usable number is the phone; anything else is kept for the notes.</summary>
    public static (string? Phone, List<string> Others, List<string> Invalid) SplitPhones(string? text)
    {
        string? first = null; var others = new List<string>(); var invalid = new List<string>();
        foreach (var part in PhoneSeparators().Split(text ?? "").Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (!Tenant.IsValidPhone(part)) { invalid.Add(part); continue; }
            if (first is null) first = part; else others.Add(part);
        }
        return (first, others, invalid);
    }

    /// <summary>Text as the person sees it: whole numbers without ".0" (a unit "101" is not "101.0"), trimmed, with inner whitespace tidied.</summary>
    public static string Clean(SheetCell cell)
    {
        if (cell.Number is { } n && !cell.IsDate && n == Math.Floor(n) && Math.Abs(n) < 1e12) return ((long)n).ToString(CultureInfo.InvariantCulture);
        return string.Join(' ', cell.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    // ---------- a whole row ----------

    public static ParsedRow ParseRow(SheetRow row, IReadOnlyDictionary<ImportColumn, int> map)
    {
        SheetCell Cell(ImportColumn c) => map.TryGetValue(c, out var i) && i < row.Cells.Count ? row.Cells[i] : default;
        var p = new ParsedRow { Row = row.RowNumber };

        p.Name = PersonName.Display(Clean(Cell(ImportColumn.Name)));
        if (p.Name.Length == 0) p.Errors.Add("Falta el nombre.");
        else if (p.Name.Length > 200) p.Errors.Add("El nombre supera 200 caracteres.");

        p.UnitCode = Clean(Cell(ImportColumn.Unit));
        if (p.UnitCode.Length == 0) p.Errors.Add("Falta el departamento.");
        else if (p.UnitCode.Length > 30) p.Errors.Add("La clave del departamento supera 30 caracteres.");

        var (rent, rentProblem) = ParseMoney(Cell(ImportColumn.Rent));
        if (rentProblem is not null) p.Errors.Add($"Renta: {rentProblem}"); else if (rent is null) p.Errors.Add("Falta la renta."); else p.Rent = rent;

        var (start, startProblem) = ParseDate(Cell(ImportColumn.Start));
        if (startProblem is not null) p.Errors.Add($"Inicio: {startProblem}"); else if (start is null) p.Errors.Add("Falta la fecha de inicio."); else p.Start = start;

        if (map.ContainsKey(ImportColumn.End))
        {
            var (end, endProblem) = ParseDate(Cell(ImportColumn.End));
            if (endProblem is not null) p.Errors.Add($"Término: {endProblem}"); else p.End = end;
        }
        if (p.Start is { } s && p.End is { } e && e < s) p.Errors.Add("La fecha de término es anterior al inicio.");

        if (map.ContainsKey(ImportColumn.DueDay))
        {
            var (day, dayProblem) = ParseDueDay(Cell(ImportColumn.DueDay));
            if (dayProblem is not null) p.Errors.Add($"Fecha de pago: {dayProblem}"); else p.DueDay = day;
        }
        if (p.DueDay is null && !p.Errors.Any(e => e.StartsWith("Fecha de pago")) && p.Start is { } st)
        {
            p.DueDay = Math.Min(st.Day, 28);
            p.Warnings.Add($"No traía día de pago: se usará el día {p.DueDay}, el del inicio del contrato.");
        }

        if (map.ContainsKey(ImportColumn.Deposit))
        {
            var (deposit, depositProblem) = ParseMoney(Cell(ImportColumn.Deposit));
            if (depositProblem is not null) p.Errors.Add($"Depósito: {depositProblem}"); else p.Deposit = deposit;
        }

        var (phone, others, invalid) = SplitPhones(Cell(ImportColumn.Phone).Text);
        p.Phone = phone;
        if (invalid.Count > 0) p.Warnings.Add($"Teléfono no válido, no se guardó como teléfono: {string.Join(", ", invalid)}.");
        var notes = new List<string>();
        if (others.Count > 0) notes.Add($"Otros teléfonos: {string.Join(", ", others)}.");
        if (invalid.Count > 0) notes.Add($"Teléfono sin formato válido en el archivo: {string.Join(", ", invalid)}.");
        p.Notes = notes.Count == 0 ? null : string.Join(" ", notes); // tenant notes

        var email = Clean(Cell(ImportColumn.Email));
        if (email.Length > 0) { var normalized = Tenant.NormalizeEmail(email)!; if (Tenant.IsValidEmail(normalized)) p.Email = normalized; else p.Warnings.Add($"Correo no válido, se omitió: {email}."); }

        var guarantor = Clean(Cell(ImportColumn.Guarantor));
        if (guarantor.Length > 200) p.Errors.Add("El nombre del fiador supera 200 caracteres.");
        else if (guarantor.Length > 0) p.Guarantor = guarantor;
        var guarantorPhoneText = Cell(ImportColumn.GuarantorPhone).Text.Trim();
        if (guarantorPhoneText.Length > 0)
        {
            var (gp, _, gInvalid) = SplitPhones(guarantorPhoneText);
            if (p.Guarantor is null) p.Warnings.Add("Hay teléfono de fiador pero no su nombre: se omitió.");
            else if (gp is null) p.Warnings.Add($"Teléfono del fiador no válido, se omitió: {string.Join(", ", gInvalid)}.");
            else p.GuarantorPhone = gp;
        }
        return p;
    }

    /// <summary>The lease notes: what the sheet says under "Observaciones" (kept as the contract's conditions).</summary>
    public static string? LeaseNotes(SheetRow row, IReadOnlyDictionary<ImportColumn, int> map) =>
        map.TryGetValue(ImportColumn.Notes, out var i) && i < row.Cells.Count && Clean(row.Cells[i]) is { Length: > 0 } t ? t : null;
}
