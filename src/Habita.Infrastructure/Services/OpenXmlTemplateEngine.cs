using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Habita.Application.Abstractions;
using Habita.Domain.Documents;

namespace Habita.Infrastructure.Services;

/// <summary>
/// Fills the landlord's Word template. Word often cuts a typed field into several pieces of formatting ("{{nombre", "_completo}}"), so fields are searched in the
/// text of the whole paragraph and then written back into the pieces, keeping the formatting of the piece where the field began. A template is
/// untrusted input: before it is opened it is checked for macros, embedded objects, links to outside files and zip bombs.
/// </summary>
public sealed partial class OpenXmlTemplateEngine : IContractTemplateEngine
{
    private const long MaxUncompressedBytes = 60L * 1024 * 1024;
    private const int MaxEntries = 1000;

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}")] private static partial Regex FieldPattern();
    [GeneratedRegex(@"^(heading|ttulo|titulo|title)\s*\d*$", RegexOptions.IgnoreCase)] private static partial Regex HeadingStyle();

    // ---------- scan ----------

    public TemplateScan Scan(byte[] docx)
    {
        if (OpenXmlPackageGuard.Check(docx, "word/document.xml", "documento de Word (.docx)") is { } unsafePackage) return Refuse(unsafePackage);
        try
        {
            using var ms = new MemoryStream(docx, writable: false);
            using var doc = WordprocessingDocument.Open(ms, false);
            if (doc.MainDocumentPart?.Document?.Body is null) return Refuse("El archivo no es un documento de Word (.docx) válido.");

            var fields = new SortedSet<string>(StringComparer.Ordinal); var malformed = false;
            foreach (var root in Roots(doc))
                foreach (var p in root.Descendants<Paragraph>())
                {
                    var text = string.Concat(TextsOf(p).Select(t => t.Text));
                    foreach (Match m in FieldPattern().Matches(text)) fields.Add(m.Groups[1].Value.ToLowerInvariant());
                    var rest = FieldPattern().Replace(text, "");
                    if (rest.Contains("{{") || rest.Contains("}}")) malformed = true;
                }
            if (malformed)
                return Refuse("Hay llaves {{ }} que no forman un campo válido. Cada campo se escribe completo como {{nombre_del_campo}}, con letras, números o guion bajo y sin saltos de línea en medio.");
            return new TemplateScan([.. fields], [.. fields.Where(f => !ContractFields.IsKnown(f))], null);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return Refuse("El archivo no es un documento de Word (.docx) válido."); }
    }

    private static TemplateScan Refuse(string problem) => new([], [], problem);

    // ---------- render ----------

    public byte[] Render(byte[] docx, IReadOnlyDictionary<string, string> values)
    {
        using var ms = new MemoryStream();
        ms.Write(docx, 0, docx.Length);
        ms.Position = 0;
        using (var doc = WordprocessingDocument.Open(ms, true))
            foreach (var root in Roots(doc))
            {
                var touched = new List<Paragraph>();
                foreach (var p in root.Descendants<Paragraph>().ToList())
                    if (ReplaceFields(p, values)) touched.Add(p);
                foreach (var p in touched) PruneIfEmpty(p);
            }
        return ms.ToArray();
    }

    private static IEnumerable<OpenXmlElement> Roots(WordprocessingDocument doc)
    {
        var main = doc.MainDocumentPart!;
        yield return main.Document!.Body!;
        foreach (var h in main.HeaderParts) if (h.Header is not null) yield return h.Header;
        foreach (var f in main.FooterParts) if (f.Footer is not null) yield return f.Footer;
        if (main.FootnotesPart?.Footnotes is { } notes) yield return notes;
        if (main.EndnotesPart?.Endnotes is { } ends) yield return ends;
    }

    /// <summary>The text pieces that belong to this paragraph itself (not to a paragraph nested inside it, such as one in a text box), in reading order.</summary>
    private static List<Text> TextsOf(Paragraph p) =>
        p.Descendants<Run>().Where(r => r.Ancestors<Paragraph>().First() == p).SelectMany(r => r.Elements<Text>()).ToList();

    private static bool ReplaceFields(Paragraph p, IReadOnlyDictionary<string, string> values)
    {
        var texts = TextsOf(p);
        if (texts.Count == 0) return false;
        var joined = string.Concat(texts.Select(t => t.Text));
        var matches = FieldPattern().Matches(joined);
        if (matches.Count == 0) return false;

        // Where each piece starts in the joined text, measured BEFORE any change. Matches are replaced last to first, so a replacement never moves an earlier one,
        // and the new text is never searched again: a name that happens to contain "{{renta}}" stays a name.
        var starts = new int[texts.Count]; var offset = 0;
        for (var i = 0; i < texts.Count; i++) { starts[i] = offset; offset += texts[i].Text.Length; }

        for (var k = matches.Count - 1; k >= 0; k--)
        {
            var m = matches[k];
            var first = PieceAt(starts, texts, m.Index); var last = PieceAt(starts, texts, m.Index + m.Length - 1);
            values.TryGetValue(m.Groups[1].Value, out var value);
            var lines = (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var head = texts[first].Text[..(m.Index - starts[first])];
            var tail = texts[last].Text[(m.Index + m.Length - starts[last])..];
            for (var i = first + 1; i < last; i++) Set(texts[i], "");
            if (first != last) Set(texts[last], tail);

            Set(texts[first], head + lines[0] + (first == last && lines.Length == 1 ? tail : ""));
            OpenXmlElement anchor = texts[first];
            for (var i = 1; i < lines.Length; i++) // a line break inside the value becomes a Word line break
            {
                var br = new Break(); anchor.InsertAfterSelf(br);
                var piece = new Text(lines[i] + (i == lines.Length - 1 && first == last ? tail : "")) { Space = SpaceProcessingModeValues.Preserve };
                br.InsertAfterSelf(piece); anchor = piece;
            }
        }
        return true;
    }

    private static int PieceAt(int[] starts, List<Text> texts, int index)
    {
        for (var i = texts.Count - 1; i >= 0; i--) if (starts[i] <= index && texts[i].Text.Length + starts[i] > index) return i;
        throw new InvalidOperationException("No se encontró el fragmento de texto de un campo.");
    }

    private static void Set(Text t, string value) { t.Text = value; t.Space = SpaceProcessingModeValues.Preserve; }

    /// <summary>
    /// A paragraph that held fields and came out empty is removed (a blank signature line for a missing guarantor would be noise), and so is a heading that
    /// stood right above it ("INVENTARIO" with nothing under it). A paragraph that is the only one in a table cell stays: a cell must have one.
    /// </summary>
    private static void PruneIfEmpty(Paragraph p)
    {
        if (p.Parent is null || HasContent(p)) return;
        if (p.Parent is TableCell cell && cell.Elements<Paragraph>().Count() == 1) return;
        var previous = p.PreviousSibling();
        p.Remove();
        if (previous is Paragraph heading && IsHeading(heading) && heading.Parent is not null) heading.Remove();
    }

    private static bool HasContent(Paragraph p) =>
        p.Descendants<Text>().Any(t => !string.IsNullOrWhiteSpace(t.Text)) || p.Descendants<Drawing>().Any() || p.Descendants<Picture>().Any();

    private static bool IsHeading(Paragraph p) =>
        p.ParagraphProperties?.OutlineLevel is not null
        || (p.ParagraphProperties?.ParagraphStyleId?.Val?.Value is { } style && HeadingStyle().IsMatch(style));

    // ---------- sample ----------

    public byte[] BuildSample(IReadOnlyList<ContractFields.Field> fields)
    {
        string[] prose =
        [
            "{{fecha_actual}}",
            "Contrato que celebran {{arrendador_nombre}} (el arrendador) y {{nombre_completo}} (el arrendatario) sobre la unidad {{depto_numero}} de {{propiedad_nombre}}, ubicada en {{propiedad_direccion}}.",
            "Renta mensual: {{renta}} ({{renta_letra}}), pagadera {{fecha_pago}}.",
            "Depósito: {{deposito}} ({{deposito_letra}}).",
            "Vigencia: del {{fecha_inicio}} al {{fecha_termino}}.",
        ];
        string[] closing = ["{{arrendatario_linea}}", "{{arrendador_linea}}", "{{fiador_linea}}"];
        var used = FieldPattern().Matches(string.Join(' ', prose.Concat(closing))).Select(m => m.Groups[1].Value).Concat(["inventario", "observaciones"]).ToHashSet();

        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body());
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new Styles(
                new Style(new StyleName { Val = "Normal" }, new StyleRunProperties(new FontSize { Val = "22" })) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
                new Style(new StyleName { Val = "heading 1" }, new BasedOn { Val = "Normal" }, new StyleParagraphProperties(new OutlineLevel { Val = 0 }, new SpacingBetweenLines { Before = "240", After = "120" }),
                    new StyleRunProperties(new Bold(), new FontSize { Val = "32" })) { Type = StyleValues.Paragraph, StyleId = "Heading1" },
                new Style(new StyleName { Val = "heading 2" }, new BasedOn { Val = "Normal" }, new StyleParagraphProperties(new OutlineLevel { Val = 1 }, new SpacingBetweenLines { Before = "240", After = "80" }),
                    new StyleRunProperties(new Bold(), new FontSize { Val = "24" })) { Type = StyleValues.Paragraph, StyleId = "Heading2" });
            var body = main.Document.Body!;

            static Paragraph Para(string text, string? style = null, bool italic = false)
            {
                var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
                if (italic) run.RunProperties = new RunProperties(new Italic());
                var p = new Paragraph(run);
                if (style is not null) p.ParagraphProperties = new ParagraphProperties(new ParagraphStyleId { Val = style });
                return p;
            }

            body.Append(Para("CONTRATO DE ARRENDAMIENTO — EJEMPLO", "Heading1"));
            body.Append(Para("Este archivo es un ejemplo para mostrar los campos disponibles. Sustituye el texto por el de tu contrato antes de usarlo: Habita no redacta ni revisa el contenido legal.", italic: true));
            foreach (var line in prose) body.Append(Para(line));
            body.Append(Para("INVENTARIO", "Heading2")); body.Append(Para("{{inventario}}"));
            body.Append(Para("OBSERVACIONES", "Heading2")); body.Append(Para("{{observaciones}}"));
            foreach (var f in fields.Where(f => !used.Contains(f.Name) && !closing.Any(c => c.Contains(f.Name))))
                body.Append(Para($"{f.Description.TrimEnd('.')}: {{{{{f.Name}}}}}"));
            foreach (var line in closing) body.Append(Para(line));

            body.Append(Para("CAMPOS DISPONIBLES (borra esta sección)", "Heading2"));
            body.Append(Para("Cada campo se escribe entre dobles llaves. Esta sección solo lista los nombres (sin llaves, para que no se rellenen); bórrala antes de usar la plantilla:", italic: true));
            foreach (var f in fields) body.Append(Para($"{f.Name} — {f.Description}"));
        }
        return ms.ToArray();
    }
}
