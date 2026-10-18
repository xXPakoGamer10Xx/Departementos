using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Habita.IntegrationTests;

/// <summary>Hito 15: importing the landlord's spreadsheet. All data here is made up.</summary>
public class Hito15ImportTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    const string Header = "Nombre,Depto,Renta,Con letra,Fecha de pago,Inicio,Término,Fiador,Teléfonos,Observaciones\n";

    static byte[] Csv(params string[] rows) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Header + string.Join("\n", rows) + "\n")).ToArray();

    /// <summary>A real .xlsx: strings are shared strings, dates are Excel serial numbers with a date style.</summary>
    static byte[] Xlsx(string sheetName, string[] headers, object[][] rows, bool macro = false)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart(); wb.Workbook = new Workbook();
            var styles = wb.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new Stylesheet(new Fonts(new Font()), new Fills(new Fill(), new Fill()), new Borders(new Border()),
                new CellFormats(new CellFormat(), new CellFormat { NumberFormatId = 14, ApplyNumberFormat = true }));
            var sst = wb.AddNewPart<SharedStringTablePart>(); sst.SharedStringTable = new SharedStringTable();
            int Shared(string t) { sst.SharedStringTable.AppendChild(new SharedStringItem(new Text(t))); return sst.SharedStringTable.Count() - 1; }
            var ws = wb.AddNewPart<WorksheetPart>(); var data = new SheetData(); ws.Worksheet = new Worksheet(data);
            wb.Workbook.AppendChild(new Sheets(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1, Name = sheetName }));
            string Col(int i) => ((char)('A' + i)).ToString();
            uint r = 1;
            foreach (var line in new[] { headers.Cast<object>().ToArray() }.Concat(rows))
            {
                var row = new Row { RowIndex = r };
                for (var i = 0; i < line.Length; i++)
                {
                    var cell = new Cell { CellReference = Col(i) + r };
                    switch (line[i])
                    {
                        case string s: cell.DataType = CellValues.SharedString; cell.CellValue = new CellValue(Shared(s).ToString()); break;
                        case DateOnly d: cell.CellValue = new CellValue(d.ToDateTime(TimeOnly.MinValue).ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture)); cell.StyleIndex = 1; break;
                        case decimal m: cell.CellValue = new CellValue(m.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                        case int n: cell.CellValue = new CellValue(n.ToString()); break;
                    }
                    row.AppendChild(cell);
                }
                data.AppendChild(row); r++;
            }
            if (macro) { var vba = wb.AddNewPart<VbaProjectPart>(); using var w = vba.GetStream(); w.Write([1, 2, 3]); }
        }
        return ms.ToArray();
    }

    static readonly string[] XHeaders = ["Nombre", "Depto", "Renta", "Con letra", "Fecha de pago", "Inicio", "Término", "Fiador", "Teléfonos", "Observaciones"];

    record Env(HttpClient Owner, Guid PropertyId);
    async Task<Env> NewEnv(string tag)
    {
        var (owner, _, _) = await fx.RegisterAsync(tag);
        var pid = (await Json(await owner.PostAsJsonAsync("/api/v1/properties", new { name = "Torre Sur", address = "Calle Falsa 123" }))).GetProperty("id").GetGuid();
        return new(owner, pid);
    }

    static MultipartFormDataContent Form(byte[] file, Guid propertyId, string name = "inquilinos.csv", bool createUnits = true, bool activate = true, string? hash = null, bool skip = false)
    {
        var f = new MultipartFormDataContent();
        var part = new ByteArrayContent(file); part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        f.Add(part, "file", name);
        f.Add(new StringContent(propertyId.ToString()), "propertyId");
        f.Add(new StringContent(createUnits ? "true" : "false"), "createMissingUnits");
        f.Add(new StringContent(activate ? "true" : "false"), "activateLeases");
        f.Add(new StringContent(skip ? "true" : "false"), "skipErrors");
        if (hash is not null) f.Add(new StringContent(hash), "planHash");
        return f;
    }

    static async Task<JsonElement> Preview(Env e, byte[] file, string name = "inquilinos.csv", bool createUnits = true, bool activate = true)
    {
        var r = await e.Owner.PostAsync("/api/v1/imports/preview", Form(file, e.PropertyId, name, createUnits, activate));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Json(r);
    }

    static string Hash(JsonElement p) => p.GetProperty("planHash").GetString()!;
    static Task<HttpResponseMessage> Confirm(Env e, byte[] file, string hash, string name = "inquilinos.csv", bool skip = false, bool createUnits = true, bool activate = true) =>
        e.Owner.PostAsync("/api/v1/imports/confirm", Form(file, e.PropertyId, name, createUnits, activate, hash, skip));

    static async Task<int> Count(HttpClient c, string url) => (await Json(await c.GetAsync(url))).GetProperty("total").GetInt32();

    [Fact]
    public async Task A_csv_is_previewed_without_saving_and_then_imported_with_names_exactly_as_written()
    {
        var e = await NewEnv("imp1");
        var file = Csv(
            "ANA María  Ejemplo,101,\"$8,500.00\",ocho mil,5,01/07/2026,30/06/2027,Pedro Fiador,55 1234 5678 / 55 8765 4321,Sin prorrateo",
            "Luis Prueba,102,7000,siete mil,05 de cada mes,15/08/2026,,,,");
        var p = await Preview(e, file);
        Assert.True(p.GetProperty("canConfirm").GetBoolean());
        Assert.Equal(2, p.GetProperty("toCreate").GetInt32()); Assert.Equal(2, p.GetProperty("newTenants").GetInt32()); Assert.Equal(2, p.GetProperty("newUnits").GetInt32());
        Assert.Equal(0, await Count(e.Owner, "/api/v1/tenants"));                                       // the preview saved nothing

        var r = await Confirm(e, file, Hash(p));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var res = await Json(r);
        Assert.Equal(2, res.GetProperty("leasesCreated").GetInt32()); Assert.Equal(2, res.GetProperty("leasesActivated").GetInt32());

        var tenants = (await Json(await e.Owner.GetAsync("/api/v1/tenants?pageSize=50"))).GetProperty("items").EnumerateArray().ToList();
        var ana = tenants.Single(t => t.GetProperty("fullName").GetString()!.Contains("Ejemplo"));
        Assert.Equal("ANA María Ejemplo", ana.GetProperty("fullName").GetString());                       // name first, as stored; only the spacing is tidied
        Assert.Equal("55 1234 5678", ana.GetProperty("phone").GetString());
        Assert.Contains("55 8765 4321", ana.GetProperty("notes").GetString());                            // the second phone is not lost
        var leases = (await Json(await e.Owner.GetAsync("/api/v1/leases?pageSize=50"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, leases.Count); Assert.All(leases, l => Assert.Equal("Active", l.GetProperty("status").GetString()));
        var l1 = leases.Single(l => l.GetProperty("monthlyRent").GetDecimal() == 8500m);
        Assert.Equal("Pedro Fiador", l1.GetProperty("guarantorName").GetString()); Assert.Equal(5, l1.GetProperty("dueDay").GetInt32());
        Assert.Equal("2027-06-30", l1.GetProperty("endDate").GetString());
        var audit = await (await e.Owner.GetAsync("/api/v1/audit-logs?action=import.confirmed")).Content.ReadAsStringAsync();
        Assert.Contains("import.confirmed", audit); Assert.DoesNotContain("Ejemplo", audit); Assert.DoesNotContain("Pedro", audit);
    }

    [Fact]
    public async Task An_xlsx_with_excel_dates_and_numbers_is_read_and_the_sheet_called_Inquilinos_wins()
    {
        var e = await NewEnv("imp2");
        var file = Xlsx("Inquilinos", XHeaders, [
            ["Carla Sánchez", 201, 9000m, "nueve mil", 10, new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 31), "Raúl Aval", "55 2222 3333", "Mascota"],
        ]);
        var p = await Preview(e, file, "inquilinos.xlsx");
        Assert.Equal("Inquilinos", p.GetProperty("sheetName").GetString());
        var row = p.GetProperty("rows")[0];
        Assert.Equal("Create", row.GetProperty("action").GetString()); Assert.Equal("201", row.GetProperty("unitCode").GetString());   // 201, not 201.0
        Assert.Equal("2026-09-01", row.GetProperty("startDate").GetString()); Assert.Equal(9000m, row.GetProperty("monthlyRent").GetDecimal());
        Assert.Equal(200, (int)(await Confirm(e, file, Hash(p), "inquilinos.xlsx")).StatusCode);
    }

    [Fact]
    public async Task Importing_the_same_file_again_creates_nothing_new()
    {
        var e = await NewEnv("imp3");
        var file = Csv("Ana Ejemplo,101,8500,x,5,01/07/2026,,,,", "Luis Prueba,102,7000,x,5,01/07/2026,,,,");
        Assert.Equal(HttpStatusCode.OK, (await Confirm(e, file, Hash(await Preview(e, file)))).StatusCode);

        var again = await Preview(e, file);
        Assert.Equal(0, again.GetProperty("toCreate").GetInt32()); Assert.Equal(2, again.GetProperty("alreadyImported").GetInt32());
        Assert.False(again.GetProperty("canConfirm").GetBoolean());
        var res = await Confirm(e, file, Hash(again));
        Assert.Equal(0, (await Json(res)).GetProperty("leasesCreated").GetInt32());
        Assert.Equal(2, await Count(e.Owner, "/api/v1/leases")); Assert.Equal(2, await Count(e.Owner, "/api/v1/tenants"));
    }

    [Fact]
    public async Task The_same_person_in_two_rows_becomes_one_tenant_and_a_person_who_already_exists_is_reused()
    {
        var e = await NewEnv("imp4");
        var existing = (await Json(await e.Owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "José Pérez" }))).GetProperty("id").GetGuid();
        var file = Csv("JOSE  PEREZ,101,5000,x,5,01/07/2026,,,,", "Ana Ejemplo,102,5000,x,5,01/07/2026,,,,", "ana ejemplo,103,5000,x,5,01/07/2026,,,,");
        var p = await Preview(e, file);
        Assert.Equal(1, p.GetProperty("newTenants").GetInt32());                                         // only Ana is new; José exists, Ana's two rows are one person
        Assert.Equal(HttpStatusCode.OK, (await Confirm(e, file, Hash(p))).StatusCode);
        Assert.Equal(2, await Count(e.Owner, "/api/v1/tenants"));
        var leases = (await Json(await e.Owner.GetAsync($"/api/v1/leases?tenantId={existing}"))).GetProperty("total").GetInt32();
        Assert.Equal(1, leases);
    }

    [Fact]
    public async Task Rows_with_errors_block_the_import_unless_skipped_and_say_what_is_wrong()
    {
        var e = await NewEnv("imp5");
        var file = Csv(
            "Ana Ejemplo,101,8500,x,5,01/07/2026,,,,",
            ",102,8500,x,5,01/07/2026,,,,",                          // no name
            "Luis Prueba,103,abc,x,5,01/07/2026,,,,",                // not an amount
            "Marta Ruiz,104,8500,x,31,01/07/2026,,,,",               // day 31
            "Iván Soto,105,8500,x,5,01/07/2026,01/01/2026,,,",       // ends before it starts
            "Ana Ejemplo,101,8500,x,5,01/07/2026,,,,");              // repeats row 2 of the file
        var p = await Preview(e, file);
        Assert.False(p.GetProperty("canConfirm").GetBoolean()); Assert.Equal(5, p.GetProperty("errors").GetInt32());
        var rows = p.GetProperty("rows").EnumerateArray().ToList();
        Assert.Contains("nombre", rows[1].GetProperty("errors")[0].GetString()!.ToLower());
        Assert.Contains("importe", rows[2].GetProperty("errors")[0].GetString()!);
        Assert.Contains("28", rows[3].GetProperty("errors")[0].GetString()!);

        var refused = await Confirm(e, file, Hash(p));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(0, await Count(e.Owner, "/api/v1/leases"));
        var ok = await Confirm(e, file, Hash(p), skip: true);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var res = await Json(ok);
        Assert.Equal(1, res.GetProperty("leasesCreated").GetInt32()); Assert.Equal(5, res.GetProperty("skippedWithErrors").GetInt32());
        Assert.Equal(1, await Count(e.Owner, "/api/v1/tenants"));                                         // the people of the skipped rows were not created either
    }

    [Fact]
    public async Task Confirming_after_the_file_or_the_data_changed_is_refused_and_nothing_is_saved()
    {
        var e = await NewEnv("imp6");
        var file = Csv("Ana Ejemplo,101,8500,x,5,01/07/2026,,,,");
        var hash = Hash(await Preview(e, file));
        var other = Csv("Ana Ejemplo,101,9999,x,5,01/07/2026,,,,");
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(e, other, hash)).StatusCode);                // a different file than the one previewed
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(e, file, hash, createUnits: false)).StatusCode); // different options
        Assert.Equal(HttpStatusCode.BadRequest, (await Confirm(e, file, "")).StatusCode);                  // no preview at all
        Assert.Equal(0, await Count(e.Owner, "/api/v1/leases"));

        // The data changed between preview and confirm: someone else created that tenant's lease meanwhile.
        Assert.Equal(HttpStatusCode.OK, (await Confirm(e, file, hash)).StatusCode);
        var stale = await Confirm(e, file, hash);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, await Count(e.Owner, "/api/v1/leases"));
    }

    async Task Sql(string sql)
    {
        using var scope = fx.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Habita.Infrastructure.Persistence.HabitaDbContext>().Database.ExecuteSqlRawAsync(sql);
    }

    [Fact]
    public async Task A_failure_in_the_middle_rolls_everything_back()
    {
        var e = await NewEnv("imp7");
        var file = Csv("Ana Ejemplo,101,8500,x,5,01/07/2026,,,,", "Luis Prueba,102,8500,x,5,01/07/2026,,,,", "Marta Ruiz,103,8500,x,5,01/07/2026,,,,");
        var hash = Hash(await Preview(e, file));
        // The database refuses the second contract (something the preview could not foresee): the first row, its tenant and its unit must not survive.
        await Sql("""
            CREATE FUNCTION h15_fail_second() RETURNS trigger AS $$ BEGIN
              IF (SELECT count(*) FROM "Leases" WHERE "OrganizationId" = NEW."OrganizationId") >= 1 THEN RAISE EXCEPTION 'forced failure'; END IF; RETURN NEW; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER h15_fail_second_trg BEFORE INSERT ON "Leases" FOR EACH ROW EXECUTE FUNCTION h15_fail_second();
            """);
        try
        {
            var r = await Confirm(e, file, hash);
            Assert.False(r.IsSuccessStatusCode);
        }
        finally { await Sql("DROP TRIGGER h15_fail_second_trg ON \"Leases\"; DROP FUNCTION h15_fail_second();"); }
        Assert.Equal(0, await Count(e.Owner, "/api/v1/tenants")); Assert.Equal(0, await Count(e.Owner, "/api/v1/leases"));
        var units = (await Json(await e.Owner.GetAsync($"/api/v1/properties/{e.PropertyId}/units"))).GetProperty("total").GetInt32();
        Assert.Equal(0, units);
        // And the very same file imports fine once the cause is gone.
        Assert.Equal(HttpStatusCode.OK, (await Confirm(e, file, hash)).StatusCode);
        Assert.Equal(3, await Count(e.Owner, "/api/v1/leases"));
    }

    [Fact]
    public async Task Overlapping_contracts_for_one_unit_and_missing_units_are_reported()
    {
        var e = await NewEnv("imp8");
        var file = Csv("Ana Ejemplo,101,8500,x,5,01/07/2026,30/06/2027,,,", "Luis Prueba,101,8500,x,5,01/01/2027,,,,", "Marta Ruiz,102,8500,x,5,01/07/2026,,,,");
        var p = await Preview(e, file);
        Assert.Contains("solapa", p.GetProperty("rows")[1].GetProperty("errors")[0].GetString()!);
        var strict = await Preview(e, Csv("Ana Ejemplo,999,8500,x,5,01/07/2026,,,,"), createUnits: false);
        Assert.Contains("no existe", strict.GetProperty("rows")[0].GetProperty("errors")[0].GetString()!);
        // Not activating them: contracts stay as drafts and overlapping ones are no problem yet.
        var draft = await Preview(e, file, activate: false);
        Assert.Equal(0, draft.GetProperty("errors").GetInt32());
    }

    [Fact]
    public async Task A_row_that_overlaps_a_contract_already_in_force_is_refused()
    {
        var e = await NewEnv("imp8b");
        var first = Csv("Ana Ejemplo,101,8500,x,5,01/07/2026,30/06/2027,,,");
        Assert.Equal(HttpStatusCode.OK, (await Confirm(e, first, Hash(await Preview(e, first)))).StatusCode);

        var clash = await Preview(e, Csv("Luis Prueba,101,9000,x,5,01/01/2027,,,,"));
        Assert.Equal("Error", clash.GetProperty("rows")[0].GetProperty("action").GetString());
        Assert.Contains("Ana Ejemplo", clash.GetProperty("rows")[0].GetProperty("errors")[0].GetString()!);
        Assert.False(clash.GetProperty("canConfirm").GetBoolean());
        // The same unit after the first contract ends is fine.
        var after = await Preview(e, Csv("Luis Prueba,101,9000,x,5,01/07/2027,,,,"));
        Assert.Equal("Create", after.GetProperty("rows")[0].GetProperty("action").GetString());
    }

    [Fact]
    public async Task Bad_files_are_refused_with_a_clear_message()
    {
        var e = await NewEnv("imp9");
        async Task<string> Detail(byte[] file, string name)
        {
            var r = await e.Owner.PostAsync("/api/v1/imports/preview", Form(file, e.PropertyId, name));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            return (await Json(r)).GetProperty("detail").GetString()!;
        }
        Assert.Contains("encabezados", await Detail(Encoding.UTF8.GetBytes("hola,mundo\n1,2\n"), "x.csv"));
        Assert.Contains("obligatorias", await Detail(Encoding.UTF8.GetBytes("Nombre,Depto\nAna,101\n"), "x.csv"));
        Assert.Contains("no tiene filas", await Detail(Encoding.UTF8.GetBytes(Header), "x.csv"));
        Assert.Contains("macros", await Detail(Xlsx("Inquilinos", XHeaders, [["Ana", 1, 1m, "x", 5, new DateOnly(2026, 1, 1), "", "", "", ""]], macro: true), "x.xlsx"));
        Assert.Contains("válido", await Detail([(byte)'P', (byte)'K', 1, 2, 3, 4, 5], "x.xlsx"));
        Assert.Contains("Excel", await Detail([0, 1, 2, 3, 0, 5], "x.bin"));
        var many = Csv(Enumerable.Range(0, 501).Select(i => $"Persona {i},{1000 + i},100,x,5,01/07/2026,,,,").ToArray());
        Assert.Contains("500", await Detail(many, "x.csv"));
        Assert.Equal(HttpStatusCode.BadRequest, (await e.Owner.PostAsync("/api/v1/imports/preview", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await e.Owner.PostAsync("/api/v1/imports/preview", Form(Csv("Ana,1,1,x,5,01/07/2026,,,,"), Guid.NewGuid()))).StatusCode);
    }

    /// <summary>Replaces one part of an .xlsx with other XML, as an attacker would.</summary>
    static byte[] WithPart(byte[] xlsx, string entry, string xml)
    {
        using var input = new MemoryStream(xlsx); using var output = new MemoryStream();
        using (var zin = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Read))
        using (var zout = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var e in zin.Entries)
            {
                var copy = zout.CreateEntry(e.FullName);
                using var w = copy.Open();
                if (e.FullName == entry) w.Write(Encoding.UTF8.GetBytes(xml));
                else { using var r = e.Open(); r.CopyTo(w); }
            }
        return output.ToArray();
    }

    [Theory]
    [InlineData("xl/sharedStrings.xml")]
    [InlineData("xl/worksheets/sheet1.xml")]
    [InlineData("xl/workbook.xml")]
    [InlineData("xl/styles.xml")]
    public async Task Hostile_xml_inside_the_workbook_is_refused_quickly_and_never_expanded(string part)
    {
        var e = await NewEnv("imp12");
        var good = Xlsx("Inquilinos", XHeaders, [["Ana", 1, 1m, "x", 5, new DateOnly(2026, 1, 1), "", "", "", ""]]);
        // A "billion laughs" document: ten levels of entities that would expand to gigabytes if a parser followed them.
        var bomb = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"aaaaaaaaaa\">" +
            string.Concat(Enumerable.Range(1, 9).Select(i => $"<!ENTITY e{i} \"{string.Concat(Enumerable.Repeat(i == 1 ? "&a;" : $"&e{i - 1};", 10))}\">")) + "]><x>&e9;</x>";
        var started = System.Diagnostics.Stopwatch.StartNew();
        var r = await e.Owner.PostAsync("/api/v1/imports/preview", Form(WithPart(good, part, bomb), e.PropertyId, "x.xlsx"));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), "a hostile file must not keep the server busy");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);                              // a clear refusal, not a crash
        Assert.Equal(0, await Count(e.Owner, "/api/v1/leases"));
    }

    [Fact]
    public async Task A_workbook_that_links_to_an_outside_file_is_refused()
    {
        var e = await NewEnv("imp13");
        var good = Xlsx("Inquilinos", XHeaders, [["Ana", 1, 1m, "x", 5, new DateOnly(2026, 1, 1), "", "", "", ""]]);
        var rels = "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLink\" Target=\"file:///etc/passwd\" TargetMode=\"External\"/></Relationships>";
        var r = await e.Owner.PostAsync("/api/v1/imports/preview", Form(WithPart(good, "xl/_rels/workbook.xml.rels", rels), e.PropertyId, "x.xlsx"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("externo", (await Json(r)).GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task Only_owner_and_admin_import_and_other_organizations_are_untouched()
    {
        var e = await NewEnv("imp10");
        var (other, _, _) = await fx.RegisterAsync("imp10b");
        var file = Csv("Ana Ejemplo,101,8500,x,5,01/07/2026,,,,");
        var admin = await fx.AddMemberAsync(e.Owner, "Admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/v1/imports/preview", Form(file, e.PropertyId))).StatusCode);
        foreach (var role in new[] { "Finance", "ReadOnly", "Collector", "Maintenance" })
        {
            var c = await fx.AddMemberAsync(e.Owner, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/api/v1/imports/preview", Form(file, e.PropertyId))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/api/v1/imports/confirm", Form(file, e.PropertyId, hash: "x"))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync("/api/v1/imports/preview", Form(file, e.PropertyId))).StatusCode); // another org's property
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.NewClient().PostAsync("/api/v1/imports/preview", Form(file, e.PropertyId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await e.Owner.GetAsync("/api/v1/imports/template.csv")).StatusCode);
    }

    [Fact]
    public async Task The_template_downloaded_from_the_app_imports_cleanly()
    {
        var e = await NewEnv("imp11");
        var bytes = await (await e.Owner.GetAsync("/api/v1/imports/template.csv")).Content.ReadAsByteArrayAsync();
        var p = await Preview(e, bytes);
        Assert.Equal(0, p.GetProperty("errors").GetInt32()); Assert.Equal(1, p.GetProperty("toCreate").GetInt32());
    }
}
