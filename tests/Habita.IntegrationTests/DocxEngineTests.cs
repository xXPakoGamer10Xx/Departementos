using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Habita.Application.Documents;
using Habita.Domain.Documents;
using Habita.Domain.Leases;
using Habita.Domain.Properties;
using Habita.Domain.Settings;
using Habita.Domain.Tenants;
using Habita.Infrastructure.Services;
using Xunit;

namespace Habita.IntegrationTests;

/// <summary>The Word template engine on real .docx files (no database needed): fields split across formatting, removal of empty lines, and hostile files.</summary>
public class DocxEngineTests
{
    static readonly OpenXmlTemplateEngine Engine = new();

    // ---------- helpers: build and read real Word files ----------

    /// <summary>One paragraph whose text is cut into the given pieces, each its own run (as Word does when formatting changes in the middle). A leading "*" makes a piece bold.</summary>
    static Paragraph P(params string[] pieces) => new(pieces.Select(t => t.StartsWith('*')
        ? new Run(new RunProperties(new Bold()), new Text(t[1..]) { Space = SpaceProcessingModeValues.Preserve })
        : new Run(new Text(t) { Space = SpaceProcessingModeValues.Preserve })));

    static Paragraph Heading(string text) => new(new ParagraphProperties(new ParagraphStyleId { Val = "Heading2" }), new Run(new Text(text)));

    static byte[] Doc(Action<Body> build, Action<MainDocumentPart>? parts = null)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart(); main.Document = new Document(new Body());
            build(main.Document.Body!);
            parts?.Invoke(main);
        }
        return ms.ToArray();
    }

    static List<string> Texts(byte[] docx)
    {
        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        return doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().Select(p => string.Concat(p.Descendants<Text>().Select(t => t.Text))).ToList();
    }

    static byte[] Render(byte[] docx, params (string Key, string Value)[] values) =>
        Engine.Render(docx, values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase));

    /// <summary>A zip with exactly these entries, for the hostile-file tests.</summary>
    static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, content) in entries) { using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); w.Write(content); }
        return ms.ToArray();
    }

    /// <summary>A real, valid .docx with one extra entry slipped in.</summary>
    static byte[] DocWith(string extraName, string extraContent)
    {
        var original = Doc(b => b.Append(P("{{nombre_completo}}")));
        using var input = new MemoryStream(original); using var output = new MemoryStream();
        using (var src = new ZipArchive(input, ZipArchiveMode.Read))
        using (var dst = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var e in src.Entries) { using var a = e.Open(); using var b = dst.CreateEntry(e.FullName).Open(); a.CopyTo(b); }
            using var w = new StreamWriter(dst.CreateEntry(extraName).Open(), new UTF8Encoding(false)); w.Write(extraContent);
        }
        return output.ToArray();
    }

    // ---------- scanning ----------

    [Fact]
    public void Scan_lists_the_fields_without_caring_about_case_or_spaces_and_flags_the_unknown_ones()
    {
        var scan = Engine.Scan(Doc(b => b.Append(P("Hola {{Nombre_Completo}}, renta {{ renta }} y {{nombre_complto}} y {{renta}}"))));
        Assert.Null(scan.Problem);
        Assert.Equal(new[] { "nombre_completo", "nombre_complto", "renta" }, scan.Fields);
        Assert.Equal(new[] { "nombre_complto" }, scan.UnknownFields);
    }

    [Fact]
    public void Scan_finds_a_field_that_word_cut_into_pieces()
    {
        var scan = Engine.Scan(Doc(b => b.Append(P("{{nom", "bre_comp", "leto}}"))));
        Assert.Equal(new[] { "nombre_completo" }, scan.Fields);
    }

    [Fact]
    public void Scan_reads_headers_and_footers_too()
    {
        var docx = Doc(b => b.Append(P("texto")), main =>
        {
            main.AddNewPart<HeaderPart>().Header = new Header(P("Contrato de {{propiedad_nombre}}"));
            main.AddNewPart<FooterPart>().Footer = new Footer(P("{{arrendador_nombre}}"));
        });
        Assert.Equal(new[] { "arrendador_nombre", "propiedad_nombre" }, Engine.Scan(docx).Fields);
    }

    [Theory]
    [InlineData("Falta cerrar {{renta")]
    [InlineData("Sobra una llave renta}}")]
    [InlineData("Campo con guion {{nombre-completo}}")]
    [InlineData("Campo vacío {{}}")]
    [InlineData("Campo con espacio {{nombre completo}}")]
    public void Scan_refuses_braces_that_do_not_form_a_field(string text)
    {
        var scan = Engine.Scan(Doc(b => b.Append(P(text))));
        Assert.NotNull(scan.Problem);
        Assert.Contains("llaves", scan.Problem);
    }

    // ---------- filling ----------

    [Fact]
    public void A_field_split_across_several_pieces_is_replaced_and_keeps_the_formatting_of_where_it_began()
    {
        var docx = Doc(b => b.Append(P("Arrendatario: ", "*{{nombre", "_comple", "to}}", " firma.")));
        var result = Render(docx, ("nombre_completo", "Ana Ejemplo"));
        Assert.Equal(new[] { "Arrendatario: Ana Ejemplo firma." }, Texts(result));
        using var ms = new MemoryStream(result); using var doc = WordprocessingDocument.Open(ms, false);
        var bold = doc.MainDocumentPart!.Document!.Body!.Descendants<Run>().Where(r => r.RunProperties?.Bold is not null).Select(r => r.InnerText).ToList();
        Assert.Contains("Ana Ejemplo", bold);                      // the value took the bold of the first piece
        Assert.DoesNotContain(doc.MainDocumentPart.Document.Body.Descendants<Text>(), t => t.Text.Contains("{{") || t.Text.Contains("}}"));
    }

    [Fact]
    public void Many_fields_in_one_paragraph_in_one_piece_or_many_are_all_replaced()
    {
        var docx = Doc(b =>
        {
            b.Append(P("{{renta}} / {{renta_letra}} / {{dia_pago}}"));
            b.Append(P("{{re", "nta}}", " y ", "{{dia_", "pago}}", "{{renta}}"));
        });
        var result = Render(docx, ("renta", "$8,500.00"), ("renta_letra", "OCHO MIL QUINIENTOS PESOS 00/100 M.N."), ("dia_pago", "5"));
        Assert.Equal(new[] { "$8,500.00 / OCHO MIL QUINIENTOS PESOS 00/100 M.N. / 5", "$8,500.00 y 5$8,500.00" }, Texts(result));
    }

    [Fact]
    public void Special_characters_in_a_value_are_kept_as_text_and_never_break_the_file()
    {
        var weird = "<b>Pérez & \"López\"</b> ñ ü ' € 日本";
        var result = Render(Doc(b => b.Append(P("Nombre: {{nombre_completo}}"))), ("nombre_completo", weird));
        Assert.Equal(new[] { "Nombre: " + weird }, Texts(result));
        Assert.Null(Engine.Scan(result).Problem); // still a valid Word file
    }

    [Fact]
    public void A_value_that_looks_like_a_field_is_not_expanded_again()
    {
        // A tenant whose name contains "{{renta}}" must not be able to pull other data into the contract.
        var result = Render(Doc(b => b.Append(P("{{nombre_completo}} paga {{renta}}"))), ("nombre_completo", "Eve {{renta}} {{fiador_nombre}}"), ("renta", "$100.00"), ("fiador_nombre", "SECRETO"));
        Assert.Equal(new[] { "Eve {{renta}} {{fiador_nombre}} paga $100.00" }, Texts(result));
    }

    [Fact]
    public void A_value_with_line_breaks_becomes_word_line_breaks()
    {
        const string items = "Estufa\nRefrigerador\r\nCama";
        static List<string> Pieces(byte[] docx, out int breaks)
        {
            using var ms = new MemoryStream(docx); using var doc = WordprocessingDocument.Open(ms, false);
            var p = doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().Single();
            breaks = p.Descendants<Break>().Count();
            return p.Descendants<Text>().Select(t => t.Text).ToList();
        }
        // The field alone in its piece: the neighbours keep their own pieces.
        Assert.Equal(new[] { "Inventario: ", "Estufa", "Refrigerador", "Cama", " (fin)" }, Pieces(Render(Doc(b => b.Append(P("Inventario: ", "{{inventario}}", " (fin)"))), ("inventario", items)), out var b1));
        Assert.Equal(2, b1);
        // The field in the middle of one piece: the text before joins the first line and the text after joins the last.
        Assert.Equal(new[] { "Inventario: Estufa", "Refrigerador", "Cama (fin)" }, Pieces(Render(Doc(b => b.Append(P("Inventario: {{inventario}} (fin)"))), ("inventario", items)), out var b2));
        Assert.Equal(2, b2);
    }

    [Fact]
    public void Headers_and_footers_are_filled_too()
    {
        var docx = Doc(b => b.Append(P("cuerpo")), main =>
        {
            main.AddNewPart<HeaderPart>().Header = new Header(P("Contrato ", "{{propiedad_", "nombre}}"));
            main.AddNewPart<FooterPart>().Footer = new Footer(P("{{arrendador_nombre}}"));
        });
        var result = Render(docx, ("propiedad_nombre", "Torre Norte"), ("arrendador_nombre", "Paola"));
        using var ms = new MemoryStream(result); using var doc = WordprocessingDocument.Open(ms, false);
        Assert.Equal("Contrato Torre Norte", doc.MainDocumentPart!.HeaderParts.Single().Header!.InnerText);
        Assert.Equal("Paola", doc.MainDocumentPart.FooterParts.Single().Footer!.InnerText);
    }

    [Fact]
    public void A_line_left_empty_by_its_field_disappears_but_blank_lines_the_author_typed_stay()
    {
        var docx = Doc(b =>
        {
            b.Append(P("Antes"));
            b.Append(P(""));                         // typed by the author: stays
            b.Append(P("{{fiador_linea}}"));         // empty value: goes
            b.Append(P("Después"));
        });
        Assert.Equal(new[] { "Antes", "", "Después" }, Texts(Render(docx, ("fiador_linea", ""))));
        Assert.Equal(new[] { "Antes", "", "FIADOR: Pedro", "Después" }, Texts(Render(docx, ("fiador_linea", "FIADOR: Pedro"))));
    }

    [Fact]
    public void A_heading_left_with_nothing_under_it_goes_with_it_like_in_the_previous_system()
    {
        var docx = Doc(b =>
        {
            b.Append(P("Intro"));
            b.Append(Heading("INVENTARIO")); b.Append(P("{{inventario}}"));
            b.Append(Heading("OBSERVACIONES")); b.Append(P("{{observaciones}}"));
            b.Append(P("Cierre"));
        });
        Assert.Equal(new[] { "Intro", "OBSERVACIONES", "Sin novedad", "Cierre" }, Texts(Render(docx, ("inventario", ""), ("observaciones", "Sin novedad"))));
        Assert.Equal(new[] { "Intro", "Cierre" }, Texts(Render(docx, ("inventario", ""), ("observaciones", ""))));
        Assert.Equal(new[] { "Intro", "INVENTARIO", "Estufa", "OBSERVACIONES", "Sin novedad", "Cierre" }, Texts(Render(docx, ("inventario", "Estufa"), ("observaciones", "Sin novedad"))));
    }

    [Fact]
    public void A_table_cell_keeps_its_last_paragraph_and_loses_an_extra_empty_one()
    {
        var docx = Doc(b => b.Append(new Table(
            new TableRow(new TableCell(P("{{fiador_nombre}}")), new TableCell(P("{{nombre_completo}}"), P("{{fiador_telefono}}"))))));
        var result = Render(docx, ("fiador_nombre", ""), ("nombre_completo", "Ana"), ("fiador_telefono", ""));
        using var ms = new MemoryStream(result); using var doc = WordprocessingDocument.Open(ms, false);
        var cells = doc.MainDocumentPart!.Document!.Body!.Descendants<TableCell>().ToList();
        Assert.Single(cells[0].Elements<Paragraph>());          // a cell must keep one paragraph, even empty
        Assert.Equal("Ana", string.Concat(cells[1].Elements<Paragraph>().Select(p => p.InnerText)));
        Assert.Single(cells[1].Elements<Paragraph>());
    }

    [Fact]
    public void Text_without_fields_is_left_exactly_as_it_was()
    {
        var docx = Doc(b => { b.Append(P("*Título en negrita")); b.Append(P("Una cláusula con {llaves simples} y 100%")); });
        Assert.Equal(new[] { "Título en negrita", "Una cláusula con {llaves simples} y 100%" }, Texts(Render(docx)));
    }

    // ---------- the sample ----------

    [Fact]
    public void The_sample_template_is_clean_uses_every_field_and_fills_in()
    {
        var sample = Engine.BuildSample(ContractFields.All);
        var scan = Engine.Scan(sample);
        Assert.Null(scan.Problem);
        Assert.Empty(scan.UnknownFields);
        Assert.Equal(ContractFields.All.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal), scan.Fields);

        var values = ContractValuesTests.Sample();
        var filled = Texts(Engine.Render(sample, values));
        Assert.DoesNotContain(filled, t => t.Contains("{{"));
        Assert.Contains(filled, t => t.Contains("Ana Ejemplo") && t.Contains("Torre Norte"));
        Assert.Contains(filled, t => t.Contains("OCHO MIL QUINIENTOS PESOS 00/100 M.N."));
        Assert.DoesNotContain(filled, t => t == "INVENTARIO");        // empty until Hito 15: the heading went with its empty line
    }

    // ---------- hostile files ----------

    [Fact]
    public void Things_that_are_not_word_files_are_refused_politely()
    {
        foreach (var junk in new[] { Array.Empty<byte>(), "hola"u8.ToArray(), "%PDF-1.4 ..."u8.ToArray(), new byte[] { 0x50, 0x4B, 3, 4, 1, 2, 3 }, Zip(("a.txt", "hola")) })
        {
            var scan = Engine.Scan(junk);
            Assert.NotNull(scan.Problem);
            Assert.Contains("Word", scan.Problem);
        }
    }

    [Fact]
    public void A_valid_docx_passes_and_an_ordinary_web_link_is_allowed()
    {
        var withLink = DocWith("word/_rels/document.xml.rels.extra.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r9\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"https://example.com\" TargetMode=\"External\"/></Relationships>");
        Assert.Null(Engine.Scan(withLink).Problem);
    }

    [Theory]
    [InlineData("word/vbaProject.bin", "x", "macros")]
    [InlineData("word/embeddings/oleObject1.bin", "x", "incrustados")]
    [InlineData("word/activeX/activeX1.xml", "x", "incrustados")]
    [InlineData("[Content_Types].xml", "<Types><Override ContentType=\"application/vnd.ms-word.document.macroEnabled.main+xml\"/></Types>", "macros")]
    public void Macros_and_embedded_objects_are_refused(string name, string content, string word)
    {
        // For [Content_Types].xml the entry would be a duplicate of the real one, so build the package by hand.
        var bytes = name == "[Content_Types].xml"
            ? Zip((name, content), ("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>"))
            : DocWith(name, content);
        var scan = Engine.Scan(bytes);
        Assert.NotNull(scan.Problem);
        Assert.Contains(word, scan.Problem);
    }

    [Theory]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate", "file:///etc/passwd")]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/image", "http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject", "http://internal-service/x")]
    public void A_template_that_points_outside_the_file_is_refused_because_the_pdf_service_would_fetch_it(string type, string target)
    {
        var rels = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r9\" Type=\"{type}\" Target=\"{target}\" TargetMode=\"External\"/></Relationships>";
        var scan = Engine.Scan(DocWith("word/_rels/settings.xml.rels", rels));
        Assert.NotNull(scan.Problem);
        Assert.Contains("externo", scan.Problem);
    }

    [Fact]
    public void A_zip_bomb_is_refused_before_it_is_expanded()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open())) w.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>");
            using var big = zip.CreateEntry("word/media/huge.bin", CompressionLevel.SmallestSize).Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 80; i++) big.Write(zeros);       // 80 MB of zeros: a few KB once compressed
        }
        Assert.True(ms.Length < 1_000_000, "the test file itself must be small");
        var scan = Engine.Scan(ms.ToArray());
        Assert.NotNull(scan.Problem);
        Assert.Contains("grande", scan.Problem);
    }

    [Fact]
    public void Too_many_parts_and_paths_that_climb_out_are_refused()
    {
        var many = Zip(Enumerable.Range(0, 1200).Select(i => ($"word/p{i}.xml", "<a/>")).Append(("word/document.xml", "<a/>")).ToArray());
        Assert.NotNull(Engine.Scan(many).Problem);
        Assert.NotNull(Engine.Scan(Zip(("word/document.xml", "<a/>"), ("../../evil.txt", "x"))).Problem);
    }

    [Fact]
    public void A_doctype_in_a_relationships_file_cannot_be_used_to_read_local_files()
    {
        var xxe = "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">&x;</Relationships>";
        var scan = Engine.Scan(DocWith("word/_rels/evil.xml.rels", xxe));
        Assert.NotNull(scan.Problem); // refused (or at least never expanded): it must not throw or read the file
    }
}

/// <summary>What each contract field says for a real lease. Every field of the vocabulary must have a value.</summary>
public class ContractValuesTests
{
    static readonly DateOnly Today = new(2026, 10, 17);
    static Lease Lease(bool openEnded = false, decimal? deposit = 8500m, string? notes = "Renta mensual, sin prorrateo.") =>
        new() { StartDate = new(2026, 7, 1), EndDate = openEnded ? null : new DateOnly(2027, 6, 30), MonthlyRent = 8500m, DueDay = 5, DepositAmount = deposit, TermsNotes = notes };
    static readonly Tenant Ana = new() { FullName = "  Ana   Ejemplo ", Phone = "55 1234 5678", Email = "ana@example.com" };
    static readonly Unit Unit101 = new() { Code = "101" };
    static readonly Property Tower = new() { Name = "Torre Norte", Address = "Av. Demo 123, CDMX" };
    static readonly OrganizationSettings Settings = new() { LandlordName = "Paola Propietaria", LandlordAddress = "Calle 1, CDMX" };

    public static Dictionary<string, string> Sample() => ContractValues.Build(Lease(), Ana, Unit101, Tower, Settings, "MXN", Today);

    [Fact]
    public void Every_field_of_the_vocabulary_has_a_value_and_nothing_extra_is_produced()
    {
        var values = Sample();
        Assert.Equal(ContractFields.All.Select(f => f.Name).OrderBy(n => n), values.Keys.OrderBy(n => n));
    }

    [Fact]
    public void The_values_read_like_a_contract()
    {
        var v = Sample();
        Assert.Equal("Ana Ejemplo", v["nombre_completo"]);                         // name first, spaces tidied
        Assert.Equal("17 de octubre de 2026", v["fecha_actual"]);
        Assert.Equal("$8,500.00", v["renta"]); Assert.Equal("8500.00", v["renta_numero"]);
        Assert.Equal("OCHO MIL QUINIENTOS PESOS 00/100 M.N.", v["renta_letra"]);
        Assert.Equal("5", v["dia_pago"]); Assert.Equal("5 de cada mes", v["fecha_pago"]);
        Assert.Equal("1 de julio de 2026", v["fecha_inicio"]); Assert.Equal("30 de junio de 2027", v["fecha_termino"]);
        Assert.Equal("101", v["depto_numero"]); Assert.Equal("Torre Norte", v["propiedad_nombre"]); Assert.Equal("Av. Demo 123, CDMX", v["propiedad_direccion"]);
        Assert.Equal("Paola Propietaria", v["arrendador_nombre"]);
        Assert.Equal("ARRENDATARIO: Ana Ejemplo — Tel: 55 1234 5678", v["arrendatario_linea"]);
        Assert.Equal("ARRENDADOR: Paola Propietaria", v["arrendador_linea"]);
        Assert.Equal("", v["fiador_linea"]);                                         // no guarantor yet: the line (and its paragraph) disappear
        Assert.Equal("Renta mensual, sin prorrateo.", v["observaciones"]);
    }

    [Fact]
    public void Missing_data_gives_honest_text_not_a_blank_in_the_middle_of_a_sentence()
    {
        var v = ContractValues.Build(Lease(openEnded: true, deposit: null, notes: null), new Tenant { FullName = "Luis Prueba" }, Unit101, Tower, null, "MXN", Today);
        Assert.Equal("tiempo indefinido", v["fecha_termino"]);
        Assert.Equal("$0.00", v["deposito"]); Assert.Equal("CERO PESOS 00/100 M.N.", v["deposito_letra"]);
        Assert.Equal("ARRENDATARIO: Luis Prueba", v["arrendatario_linea"]);          // no " — Tel:" without a phone
        Assert.Equal("", v["arrendador_linea"]); Assert.Equal("", v["arrendador_nombre"]); Assert.Equal("", v["observaciones"]);
    }

    [Fact]
    public void Amounts_in_words_follow_the_organization_currency() =>
        Assert.Equal("OCHO MIL QUINIENTOS DÓLARES 00/100 USD", ContractValues.Build(Lease(), Ana, Unit101, Tower, Settings, "USD", Today)["renta_letra"]);
}
