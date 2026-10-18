using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Habita.Application.Abstractions;
using Habita.Domain.Organizations;
using Habita.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Habita.IntegrationTests;

/// <summary>Stands in for the PDF service: records what it was asked to convert and answers with a small file that starts like a PDF.</summary>
public class FakePdf : IPdfConverter
{
    public ConcurrentQueue<string> Html { get; } = new();
    public ConcurrentQueue<byte[]> Docx { get; } = new();
    public volatile bool Down;

    public Task<byte[]> FromDocxAsync(byte[] docx, CancellationToken ct)
    {
        if (Down) throw new PdfUnavailableException("PDF caído (simulado)");
        Docx.Enqueue(docx);
        return Task.FromResult(Encoding.ASCII.GetBytes("%PDF-1.4\n%FAKE-DOCX " + Convert.ToHexString(SHA256.HashData(docx))));
    }

    public Task<byte[]> FromHtmlAsync(string html, CancellationToken ct)
    {
        if (Down) throw new PdfUnavailableException("PDF caído (simulado)");
        Html.Enqueue(html);
        return Task.FromResult(Encoding.ASCII.GetBytes("%PDF-1.4\n%FAKE-HTML " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html)))));
    }

    /// <summary>The newest page that contains this text (several tests share one fixture, so pages are found by their own unique content).</summary>
    public string? LastHtmlWith(string text) => Html.Reverse().FirstOrDefault(h => h.Contains(text));
}

public class DocumentsFixture : ApiFixture
{
    public FakePdf Pdf { get; } = new();
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Factory = Factory.WithWebHostBuilder(b => b.ConfigureServices(s => { s.RemoveAll<IPdfConverter>(); s.AddSingleton<IPdfConverter>(Pdf); }));
    }
}

/// <summary>Hito 14: contract template and generated contracts, numbered receipts and the printable account statement.</summary>
public class Hito14Tests(DocumentsFixture fx) : IClassFixture<DocumentsFixture>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    static string D(DateOnly d) => d.ToString("yyyy-MM-dd");
    static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City")).Date);

    // ---------- scaffolding ----------

    /// <summary>A real .docx with one paragraph per string.</summary>
    static byte[] Docx(params string[] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(paragraphs.Select(t => new Paragraph(new Run(new Text(t) { Space = SpaceProcessingModeValues.Preserve })))));
        }
        return ms.ToArray();
    }

    static string DocxText(byte[] docx)
    {
        using var ms = new MemoryStream(docx); using var doc = WordprocessingDocument.Open(ms, false);
        return string.Join("\n", doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().Select(p => string.Concat(p.Descendants<Text>().Select(t => t.Text))));
    }

    static readonly string[] ContractLines =
    [
        "Contrato de {{propiedad_nombre}}, unidad {{depto_numero}}", "Arrendatario: {{nombre_completo}}", "Renta {{renta}} ({{renta_letra}}), cada {{fecha_pago}}.",
        "{{fiador_linea}}", "Notas: {{observaciones}}",
    ];

    static MultipartFormDataContent Upload(byte[] bytes, string name = "plantilla.docx")
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(Habita.Domain.Documents.ContractTemplate.DocxContentType);
        part.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"file\"; filename=\"{name}\"");
        return new MultipartFormDataContent { part };
    }

    async Task<JsonElement> UploadTemplate(HttpClient c, params string[] lines)
    {
        var r = await c.PostAsync("/api/v1/contract-template", Upload(Docx(lines.Length == 0 ? ContractLines : lines)));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Json(r);
    }

    record World(HttpClient Owner, Guid OrgId, HttpClient A, HttpClient B, Guid TenantA, Guid TenantB, Guid LeaseA, Guid LeaseB, Guid PropertyId);

    int _unit;
    async Task<Guid> Lease(HttpClient owner, Guid propertyId, Guid tenantId, string? notes = "Renta mensual, sin prorrateo.")
    {
        var uid = (await Json(await owner.PostAsJsonAsync($"/api/v1/properties/{propertyId}/units", new { code = $"D{Interlocked.Increment(ref _unit)}", baseRent = 8500 }))).GetProperty("id").GetGuid();
        var lid = (await Json(await owner.PostAsJsonAsync("/api/v1/leases", new { unitId = uid, tenantId, startDate = D(Today.AddMonths(-6)), monthlyRent = 8500, dueDay = 5, depositAmount = 8500, termsNotes = notes }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/leases/{lid}/activate", null)).StatusCode);
        return lid;
    }

    async Task<HttpClient> Login(HttpClient owner, Guid tenantId)
    {
        var code = (await Json(await owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Tenant", tenantId }))).GetProperty("code").GetString();
        var c = fx.NewClient();
        var r = await c.PostAsJsonAsync("/api/v1/auth/access-codes/redeem", new { code, email = $"inq-{Guid.NewGuid():N}@example.com", fullName = "Inquilino Portal", password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    async Task<World> NewWorld(string tag, string tenantA = "Ana Ejemplo")
    {
        fx.Pdf.Down = false;
        var (owner, orgId, _) = await fx.RegisterAsync(tag);
        var pid = (await Json(await owner.PostAsJsonAsync("/api/v1/properties", new { name = "Torre Norte", address = "Av. Demo 123" }))).GetProperty("id").GetGuid();
        var ta = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = tenantA, phone = "55 1234 5678" }))).GetProperty("id").GetGuid();
        var tb = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Luis Prueba" }))).GetProperty("id").GetGuid();
        var la = await Lease(owner, pid, ta); var lb = await Lease(owner, pid, tb);
        return new(owner, orgId, await Login(owner, ta), await Login(owner, tb), ta, tb, la, lb, pid);
    }

    static async Task<Guid> Charge(HttpClient c, Guid leaseId, decimal amount, DateOnly due, string description = "Renta")
    {
        var r = await c.PostAsJsonAsync("/api/v1/rent-charges", new { leaseId, description, amount, dueDate = D(due) });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    static async Task<JsonElement> Pay(HttpClient c, Guid tenantId, decimal amount, string? notes = null, string? reference = null, params (Guid Charge, decimal Amount)[] allocs)
    {
        var r = await c.PostAsJsonAsync("/api/v1/payments", new
        {
            tenantId, receivedOn = D(Today), amount, method = "BankTransfer", reference, notes,
            allocations = allocs.Select(a => new { chargeId = a.Charge, amount = a.Amount }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Json(r);
    }

    async Task Sql(string sql, params object[] args)
    {
        using var scope = fx.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<HabitaDbContext>().Database.ExecuteSqlRawAsync(sql, args);
    }

    async Task<T> Db<T>(Func<HabitaDbContext, Task<T>> work)
    {
        using var scope = fx.Factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<HabitaDbContext>());
    }

    static string Clabe(int n)
    {
        var first17 = "0721800" + n.ToString("D10"); int[] w = [3, 7, 1]; var sum = 0;
        for (var i = 0; i < 17; i++) sum += ((first17[i] - '0') * w[i % 3]) % 10;
        return first17 + ((10 - sum % 10) % 10);
    }

    // ---------- contract template ----------

    [Fact]
    public async Task The_template_is_uploaded_by_owner_and_admin_and_read_by_those_who_read_contracts()
    {
        var w = await NewWorld("tpl");
        var admin = await fx.AddMemberAsync(w.Owner, "Admin");
        var finance = await fx.AddMemberAsync(w.Owner, "Finance"); var readOnly = await fx.AddMemberAsync(w.Owner, "ReadOnly");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector"); var maintenance = await fx.AddMemberAsync(w.Owner, "Maintenance");

        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.GetAsync("/api/v1/contract-template")).StatusCode); // none yet
        var t = await UploadTemplate(admin);
        Assert.True(t.GetProperty("isActive").GetBoolean());
        Assert.Equal("plantilla.docx", t.GetProperty("name").GetString());
        Assert.Equal(new[] { "depto_numero", "fecha_pago", "fiador_linea", "nombre_completo", "observaciones", "propiedad_nombre", "renta", "renta_letra" },
            t.GetProperty("fields").EnumerateArray().Select(x => x.GetString()!));
        Assert.Equal(new[] { "fecha_inicio" }, t.GetProperty("missingRecommended").EnumerateArray().Select(x => x.GetString()!)); // a contract that never says when it starts

        foreach (var reader in new[] { w.Owner, admin, finance, readOnly })
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/contract-template")).StatusCode);
        foreach (var blocked in new[] { collector, maintenance, w.A })
            Assert.Equal(HttpStatusCode.Forbidden, (await blocked.GetAsync("/api/v1/contract-template")).StatusCode);
        foreach (var cannot in new[] { finance, readOnly, collector, maintenance, w.A })
            Assert.Equal(HttpStatusCode.Forbidden, (await cannot.PostAsync("/api/v1/contract-template", Upload(Docx("{{renta}}")))).StatusCode);

        var fields = await Json(await finance.GetAsync("/api/v1/contract-template/fields"));
        Assert.Equal(Habita.Domain.Documents.ContractFields.All.Count, fields.GetArrayLength());
        Assert.Contains(fields.EnumerateArray(), f => f.GetProperty("name").GetString() == "renta_letra");
    }

    [Fact]
    public async Task A_template_with_a_wrong_field_a_broken_brace_or_no_fields_is_refused_with_a_clear_message()
    {
        var w = await NewWorld("tplbad");
        async Task<(HttpStatusCode, string)> Try(byte[] bytes) { var r = await w.Owner.PostAsync("/api/v1/contract-template", Upload(bytes)); return (r.StatusCode, (await Json(r)).GetProperty("detail").GetString()!); }

        var (c1, m1) = await Try(Docx("Hola {{nombre_complto}} y {{renta}}"));
        Assert.Equal(HttpStatusCode.BadRequest, c1); Assert.Contains("{{nombre_complto}}", m1); Assert.DoesNotContain("{{renta}}", m1);
        var (c2, m2) = await Try(Docx("Falta cerrar {{renta"));
        Assert.Equal(HttpStatusCode.BadRequest, c2); Assert.Contains("llaves", m2);
        var (c3, m3) = await Try(Docx("Un contrato sin campos"));
        Assert.Equal(HttpStatusCode.BadRequest, c3); Assert.Contains("ningún campo", m3);
        var (c4, m4) = await Try("%PDF-1.4 no soy docx"u8.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, c4); Assert.Contains("Word", m4);
        var (c5, _) = await Try([]);
        Assert.Equal(HttpStatusCode.BadRequest, c5);
        var tooBig = Docx("{{renta}}").Concat(new byte[5 * 1024 * 1024]).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await Try(tooBig)).Item1);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.GetAsync("/api/v1/contract-template")).StatusCode); // nothing was saved
    }

    [Fact]
    public async Task A_new_template_retires_the_previous_one_which_stays_downloadable()
    {
        var w = await NewWorld("tplhist");
        var first = await UploadTemplate(w.Owner, "A {{renta}}");
        var second = await UploadTemplate(w.Owner, "B {{renta}} {{nombre_completo}}");
        Assert.Equal(second.GetProperty("id").GetGuid(), (await Json(await w.Owner.GetAsync("/api/v1/contract-template"))).GetProperty("id").GetGuid());
        var history = (await Json(await w.Owner.GetAsync("/api/v1/contract-template/history"))).EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        Assert.Single(history, h => h.GetProperty("isActive").GetBoolean());
        Assert.NotNull(history.Single(h => h.GetProperty("id").GetGuid() == first.GetProperty("id").GetGuid()).GetProperty("retiredAt").GetString());
        var download = await w.Owner.GetAsync($"/api/v1/contract-template/{first.GetProperty("id").GetGuid()}/file");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("A {{renta}}", DocxText(await download.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Templates_uploaded_at_the_same_time_leave_exactly_one_active()
    {
        var w = await NewWorld("tplrace");
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => w.Owner.PostAsync("/api/v1/contract-template", Upload(Docx($"Versión {i} {{{{renta}}}}"), $"v{i}.docx"))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var history = (await Json(await w.Owner.GetAsync("/api/v1/contract-template/history"))).EnumerateArray().ToList();
        Assert.Equal(6, history.Count);
        Assert.Single(history, h => h.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task The_sample_template_downloads_and_is_itself_a_valid_template()
    {
        var w = await NewWorld("tplsample");
        var sample = await w.Owner.GetAsync("/api/v1/contract-template/sample");
        Assert.Equal(HttpStatusCode.OK, sample.StatusCode);
        var bytes = await sample.Content.ReadAsByteArrayAsync();
        var up = await w.Owner.PostAsync("/api/v1/contract-template", Upload(bytes, "ejemplo.docx"));
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        Assert.Equal(Habita.Domain.Documents.ContractFields.All.Count, (await Json(up)).GetProperty("fields").GetArrayLength());
    }

    [Fact]
    public async Task Templates_are_private_to_their_organization()
    {
        var w = await NewWorld("tpliso");
        var (other, _, _) = await fx.RegisterAsync("tpliso2");
        var t = await UploadTemplate(w.Owner);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/v1/contract-template")).StatusCode);
        Assert.Equal(0, (await Json(await other.GetAsync("/api/v1/contract-template/history"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/contract-template/{t.GetProperty("id").GetGuid()}/file")).StatusCode);
    }

    // ---------- generated contracts ----------

    [Fact]
    public async Task Generating_a_contract_fills_the_template_writes_both_files_and_keeps_every_version()
    {
        var w = await NewWorld("gen");
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsync($"/api/v1/leases/{w.LeaseA}/documents", null)).StatusCode); // no template yet
        await UploadTemplate(w.Owner);

        var one = await w.Owner.PostAsync($"/api/v1/leases/{w.LeaseA}/documents", null);
        Assert.Equal(HttpStatusCode.Created, one.StatusCode);
        var v1 = await Json(one);
        Assert.Equal(1, v1.GetProperty("version").GetInt32());
        Assert.Equal("plantilla.docx", v1.GetProperty("templateName").GetString());
        Assert.Equal("Usuario Prueba", v1.GetProperty("generatedByName").GetString());
        var docxId = v1.GetProperty("id").GetGuid();

        var docx1 = await w.Owner.GetAsync($"/api/v1/leases/{w.LeaseA}/documents/{docxId}/docx");
        var bytes1 = await docx1.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, docx1.StatusCode);
        Assert.Contains("attachment", docx1.Content.Headers.ContentDisposition?.ToString());
        var text = DocxText(bytes1);
        Assert.Contains("Contrato de Torre Norte, unidad D", text);
        Assert.Contains("Arrendatario: Ana Ejemplo", text);
        Assert.Contains("Renta $8,500.00 (OCHO MIL QUINIENTOS PESOS 00/100 M.N.), cada 5 de cada mes.", text);
        Assert.Contains("Notas: Renta mensual, sin prorrateo.", text);
        Assert.DoesNotContain("{{", text);
        Assert.DoesNotContain("FIADOR", text);                            // the guarantor line was empty and its paragraph is gone
        Assert.Equal(5 - 1, text.Split('\n').Length);

        var pdf1 = await w.Owner.GetAsync($"/api/v1/leases/{w.LeaseA}/documents/{docxId}/pdf");
        var pdfBytes1 = await pdf1.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdfBytes1, 0, 5));
        Assert.Equal("application/pdf", pdf1.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes1)), Encoding.ASCII.GetString(pdfBytes1).Split("FAKE-DOCX ")[1]); // the PDF was made from exactly that Word file

        // Change the lease, replace the template, generate again: version 2 differs, version 1 is exactly what it was.
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{w.LeaseA}", new { monthlyRent = 9000 })).StatusCode);
        await UploadTemplate(w.Owner, "NUEVA {{renta}}");
        var v2 = await Json(await w.Owner.PostAsync($"/api/v1/leases/{w.LeaseA}/documents", null));
        Assert.Equal(2, v2.GetProperty("version").GetInt32());
        Assert.Contains("NUEVA $9,000.00", DocxText(await (await w.Owner.GetAsync($"/api/v1/leases/{w.LeaseA}/documents/{v2.GetProperty("id").GetGuid()}/docx")).Content.ReadAsByteArrayAsync()));
        var again = await w.Owner.GetAsync($"/api/v1/leases/{w.LeaseA}/documents/{docxId}/docx");
        Assert.Equal(bytes1, await again.Content.ReadAsByteArrayAsync());
        Assert.Equal(v1.GetProperty("docxSha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes1)));

        var list = (await Json(await w.Owner.GetAsync($"/api/v1/leases/{w.LeaseA}/documents"))).EnumerateArray().ToList();
        Assert.Equal(new[] { 2, 1 }, list.Select(d => d.GetProperty("version").GetInt32()));
        Assert.Empty((await Json(await w.Owner.GetAsync($"/api/v1/leases/{w.LeaseB}/documents"))).EnumerateArray()); // Luis's lease has none

        var audit = await (await w.Owner.GetAsync("/api/v1/audit-logs?entityType=LeaseDocument")).Content.ReadAsStringAsync();
        Assert.Contains("lease_document.generated", audit);
        Assert.DoesNotContain("Ana Ejemplo", audit);                      // the audit says what happened, not what the contract says
    }

    [Fact]
    public async Task Versions_generated_at_the_same_time_are_numbered_one_to_n_without_repeats()
    {
        var w = await NewWorld("genrace");
        await UploadTemplate(w.Owner);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => w.Owner.PostAsync($"/api/v1/leases/{w.LeaseA}/documents", null)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var versions = (await Json(await w.Owner.GetAsync($"/api/v1/leases/{w.LeaseA}/documents"))).EnumerateArray().Select(d => d.GetProperty("version").GetInt32()).OrderBy(v => v).ToList();
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, versions);
    }

    [Fact]
    public async Task Contract_documents_follow_the_roles_and_the_organization()
    {
        var w = await NewWorld("genroles");
        var admin = await fx.AddMemberAsync(w.Owner, "Admin"); var finance = await fx.AddMemberAsync(w.Owner, "Finance"); var readOnly = await fx.AddMemberAsync(w.Owner, "ReadOnly");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector"); var maintenance = await fx.AddMemberAsync(w.Owner, "Maintenance");
        var (other, _, _) = await fx.RegisterAsync("genroles2");
        await UploadTemplate(w.Owner);
        var doc = await Json(await admin.PostAsync($"/api/v1/leases/{w.LeaseA}/documents", null));   // an Admin can generate
        var path = $"/api/v1/leases/{w.LeaseA}/documents";
        var id = doc.GetProperty("id").GetGuid();

        foreach (var cannotGenerate in new[] { finance, readOnly, collector, maintenance, w.A })
            Assert.Equal(HttpStatusCode.Forbidden, (await cannotGenerate.PostAsync(path, null)).StatusCode);
        foreach (var reader in new[] { finance, readOnly })
        {
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{path}/{id}/pdf")).StatusCode);
        }
        foreach (var blocked in new[] { collector, maintenance, w.A, w.B }) Assert.Equal(HttpStatusCode.Forbidden, (await blocked.GetAsync($"{path}/{id}/pdf")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{path}/{id}/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.GetAsync($"/api/v1/leases/{w.LeaseB}/documents/{id}/pdf")).StatusCode); // right document id, wrong lease
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.PostAsync($"/api/v1/leases/{Guid.NewGuid()}/documents", null)).StatusCode);
    }

    [Fact]
    public async Task A_cancelled_lease_makes_no_contract_and_a_pdf_outage_leaves_no_trace()
    {
        var w = await NewWorld("genfail");
        await UploadTemplate(w.Owner);
        fx.Pdf.Down = true;
        var down = await w.Owner.PostAsync($"/api/v1/leases/{w.LeaseA}/documents", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Contains("PDF", (await Json(down)).GetProperty("detail").GetString());
        Assert.Empty((await Json(await w.Owner.GetAsync($"/api/v1/leases/{w.LeaseA}/documents"))).EnumerateArray());
        fx.Pdf.Down = false;
        Assert.Equal(1, (await Json(await w.Owner.PostAsync($"/api/v1/leases/{w.LeaseA}/documents", null))).GetProperty("version").GetInt32()); // the number was not used up

        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseB}/cancel", new { reason = "Error de captura" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsync($"/api/v1/leases/{w.LeaseB}/documents", null)).StatusCode);
    }

    [Fact]
    public async Task A_tenant_name_that_looks_like_a_field_or_markup_stays_a_name_in_the_contract()
    {
        var w = await NewWorld("genevil", tenantA: "Eve {{renta}} <b>&</b>");
        await UploadTemplate(w.Owner);
        var doc = await Json(await w.Owner.PostAsync($"/api/v1/leases/{w.LeaseA}/documents", null));
        var text = DocxText(await (await w.Owner.GetAsync($"/api/v1/leases/{w.LeaseA}/documents/{doc.GetProperty("id").GetGuid()}/docx")).Content.ReadAsByteArrayAsync());
        Assert.Contains("Arrendatario: Eve {{renta}} <b>&</b>", text);
    }

    [Fact]
    public async Task Generated_contracts_and_receipts_cannot_be_changed_or_erased_in_the_database()
    {
        var w = await NewWorld("immut14");
        await UploadTemplate(w.Owner);
        var doc = await Json(await w.Owner.PostAsync($"/api/v1/leases/{w.LeaseA}/documents", null));
        var pay = await Pay(w.Owner, w.TenantA, 100m);
        var docId = doc.GetProperty("id").GetGuid(); var payId = pay.GetProperty("id").GetGuid();
        var templateId = (await Json(await w.Owner.GetAsync("/api/v1/contract-template"))).GetProperty("id").GetGuid();

        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"LeaseDocuments\" SET \"Version\" = 99 WHERE \"Id\" = {0}", docId));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("DELETE FROM \"LeaseDocuments\" WHERE \"Id\" = {0}", docId));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"Receipts\" SET \"Folio\" = 500 WHERE \"PaymentId\" = {0}", payId));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("DELETE FROM \"Receipts\" WHERE \"PaymentId\" = {0}", payId));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("DELETE FROM \"ContractTemplates\" WHERE \"Id\" = {0}", templateId));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"ContractTemplates\" SET \"Name\" = 'otro.docx' WHERE \"Id\" = {0}", templateId));
        // The files behind a generated contract cannot be deleted from under it either (foreign key).
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("DELETE FROM \"StoredFiles\" WHERE \"Id\" IN (SELECT \"PdfFileId\" FROM \"LeaseDocuments\" WHERE \"Id\" = {0})", docId));
        // A retired template cannot come back.
        await UploadTemplate(w.Owner, "Otra {{renta}}");
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"ContractTemplates\" SET \"IsActive\" = TRUE, \"RetiredAt\" = NULL WHERE \"Id\" = {0}", templateId));
    }

    [Fact]
    public async Task A_tenant_downloads_only_the_contract_of_their_own_lease()
    {
        var w = await NewWorld("tcontract");
        Assert.Equal(HttpStatusCode.NotFound, (await w.A.GetAsync("/api/v1/portal/contract.pdf")).StatusCode);   // nothing generated yet
        await UploadTemplate(w.Owner);
        await w.Owner.PostAsync($"/api/v1/leases/{w.LeaseA}/documents", null);
        await w.Owner.PostAsync($"/api/v1/leases/{w.LeaseA}/documents", null);

        var mine = await w.A.GetAsync("/api/v1/portal/contract.pdf");
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal("application/pdf", mine.Content.Headers.ContentType?.MediaType);
        var docs = (await Json(await w.Owner.GetAsync($"/api/v1/leases/{w.LeaseA}/documents"))).EnumerateArray().ToList();
        var latest = docs.OrderByDescending(d => d.GetProperty("version").GetInt32()).First();
        Assert.Equal(latest.GetProperty("pdfSha256").GetString(), Convert.ToHexString(SHA256.HashData(await mine.Content.ReadAsByteArrayAsync()))); // the newest version
        Assert.Equal(HttpStatusCode.NotFound, (await w.B.GetAsync("/api/v1/portal/contract.pdf")).StatusCode);   // Luis has none: Ana's is never his
    }

    // ---------- receipts ----------

    [Fact]
    public async Task Every_payment_gets_the_next_receipt_number_of_its_organization()
    {
        var w = await NewWorld("folio");
        var (other, _, _) = await fx.RegisterAsync("folio2");
        var otherTenant = (await Json(await other.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Otro Inquilino" }))).GetProperty("id").GetGuid();

        var p1 = await Pay(w.Owner, w.TenantA, 100m); var p2 = await Pay(w.Owner, w.TenantB, 200m); var p3 = await Pay(w.Owner, w.TenantA, 300m);
        Assert.Equal(new[] { "R-000001", "R-000002", "R-000003" }, new[] { p1, p2, p3 }.Select(p => p.GetProperty("receiptFolio").GetString()));
        Assert.Equal("R-000001", (await Pay(other, otherTenant, 50m)).GetProperty("receiptFolio").GetString());     // each organization counts on its own

        var info = await Json(await w.Owner.GetAsync($"/api/v1/payments/{p2.GetProperty("id").GetGuid()}/receipt"));
        Assert.Equal(2, info.GetProperty("folio").GetInt32()); Assert.Equal("R-000002", info.GetProperty("folioText").GetString()); Assert.False(info.GetProperty("cancelled").GetBoolean());
        var list = (await Json(await w.Owner.GetAsync("/api/v1/payments?pageSize=10"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, list.Count(p => p.GetProperty("receiptFolio").GetString()!.StartsWith("R-")));
        Assert.Equal("R-000003", (await Json(await w.Owner.GetAsync($"/api/v1/payments/{p3.GetProperty("id").GetGuid()}"))).GetProperty("receiptFolio").GetString());
    }

    [Fact]
    public async Task Payments_recorded_at_the_same_time_get_different_consecutive_numbers()
    {
        var w = await NewWorld("folioraceX");
        var results = await Task.WhenAll(Enumerable.Range(1, 12).Select(i => w.Owner.PostAsJsonAsync("/api/v1/payments", new { tenantId = w.TenantA, receivedOn = D(Today), amount = 10m + i, method = "Cash" })));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var folios = (await Task.WhenAll(results.Select(Json))).Select(p => p.GetProperty("receiptFolio").GetString()!).OrderBy(f => f).ToList();
        Assert.Equal(Enumerable.Range(1, 12).Select(i => $"R-{i:D6}"), folios);       // 1..12, none repeated, none skipped
    }

    [Fact]
    public async Task A_retried_payment_does_not_use_a_second_number_and_a_rolled_back_number_is_given_back()
    {
        var w = await NewWorld("foliogap");
        async Task<HttpResponseMessage> Send(string key, decimal amount)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments") { Content = JsonContent.Create(new { tenantId = w.TenantA, receivedOn = D(Today), amount, method = "Cash" }) };
            req.Headers.Add("Idempotency-Key", key);
            return await w.Owner.SendAsync(req);
        }
        var first = await Send("k-1", 100m);
        var retry = await Send("k-1", 100m);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode); Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("R-000001", (await Json(retry)).GetProperty("receiptFolio").GetString());
        Assert.Equal("R-000002", (await Json(await Send("k-2", 200m))).GetProperty("receiptFolio").GetString());   // the retry did not burn number 2

        // A payment that fails after taking its number must give it back: take one inside a transaction and roll it back.
        int taken, again;
        using (var scope = fx.Factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ICurrentOrganization>().Set(w.OrgId, Role.Owner, OrganizationStatus.Active);
            var db = scope.ServiceProvider.GetRequiredService<HabitaDbContext>(); var folios = scope.ServiceProvider.GetRequiredService<IReceiptFolios>();
            await using (var tx = await db.Database.BeginTransactionAsync()) { taken = await folios.NextAsync(default); await tx.RollbackAsync(); }
            await using (var tx = await db.Database.BeginTransactionAsync()) { again = await folios.NextAsync(default); await tx.RollbackAsync(); }
            await Assert.ThrowsAsync<InvalidOperationException>(() => folios.NextAsync(default)); // never outside the payment's own transaction
        }
        Assert.Equal(3, taken); Assert.Equal(3, again);
        Assert.Equal("R-000003", (await Json(await Send("k-3", 300m))).GetProperty("receiptFolio").GetString());
    }

    [Fact]
    public async Task Payments_that_existed_before_receipts_are_numbered_in_the_order_they_were_recorded()
    {
        // The same statements the migration runs, applied to payments whose receipts are removed to look like a database from before Hito 14.
        var w = await NewWorld("backfill");
        var ids = new List<Guid>();
        foreach (var amount in new[] { 10m, 20m, 30m }) { ids.Add((await Pay(w.Owner, w.TenantA, amount)).GetProperty("id").GetGuid()); await Task.Delay(30); }
        await Sql("ALTER TABLE \"Receipts\" DISABLE TRIGGER receipts_immutable_trg");
        try
        {
            await Sql("DELETE FROM \"Receipts\" WHERE \"OrganizationId\" = {0}", w.OrgId);
            await Sql("DELETE FROM \"ReceiptCounters\" WHERE \"OrganizationId\" = {0}", w.OrgId);
            await Sql("""
                INSERT INTO "Receipts" ("Id", "OrganizationId", "PaymentId", "Folio", "IssuedAt")
                SELECT gen_random_uuid(), "OrganizationId", "Id", ROW_NUMBER() OVER (PARTITION BY "OrganizationId" ORDER BY "CreatedAt", "Id"), "CreatedAt"
                FROM "Payments" WHERE "OrganizationId" = {0};
                INSERT INTO "ReceiptCounters" ("OrganizationId", "LastFolio")
                SELECT "OrganizationId", MAX("Folio") FROM "Receipts" WHERE "OrganizationId" = {0} GROUP BY "OrganizationId";
                """, w.OrgId);
        }
        finally { await Sql("ALTER TABLE \"Receipts\" ENABLE TRIGGER receipts_immutable_trg"); }
        var folios = new List<string>();
        foreach (var id in ids) folios.Add((await Json(await w.Owner.GetAsync($"/api/v1/payments/{id}"))).GetProperty("receiptFolio").GetString()!);
        Assert.Equal(new[] { "R-000001", "R-000002", "R-000003" }, folios);
        Assert.Equal("R-000004", (await Pay(w.Owner, w.TenantA, 40m)).GetProperty("receiptFolio").GetString()); // the counter continues after the old ones
    }

    [Fact]
    public async Task The_receipt_shows_what_the_tenant_needs_and_nothing_internal()
    {
        var w = await NewWorld("receipt");
        await w.Owner.PatchAsJsonAsync("/api/v1/settings", new { landlordName = "Paola Propietaria", landlordAddress = "Calle 1, CDMX" });
        await w.Owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "Titular Secreto", clabe = Clabe(77) });
        var charge = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-3), "Renta de octubre");
        var pay = await Pay(w.Owner, w.TenantA, 1250m, notes: "NOTA-INTERNA-SECRETA", reference: "SPEI-12345", (charge, 1000m));
        var id = pay.GetProperty("id").GetGuid();

        var r = await w.Owner.GetAsync($"/api/v1/payments/{id}/receipt.pdf");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/pdf", r.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", r.Content.Headers.ContentDisposition?.ToString());
        Assert.Contains("Recibo R-000001.pdf", r.Content.Headers.ContentDisposition?.ToString());
        var html = fx.Pdf.LastHtmlWith("SPEI-12345")!;
        Assert.Contains("R-000001", html);
        Assert.Contains("Ana Ejemplo", html);
        Assert.Contains("Paola Propietaria", html); Assert.Contains("Calle 1, CDMX", html);
        Assert.Contains("$1,250.00", html); Assert.Contains("MIL DOSCIENTOS CINCUENTA PESOS 00/100 M.N.", html);
        Assert.Contains("Renta de octubre", html); Assert.Contains("$1,000.00", html);
        Assert.Contains("Saldo a favor de este pago", html); Assert.Contains("$250.00", html);
        Assert.Contains("No es una factura (CFDI)", html);
        Assert.DoesNotContain("CANCELADO", html);
        foreach (var secret in new[] { "NOTA-INTERNA-SECRETA", Clabe(77), "Titular Secreto", "BanCoppel", "Banorte" }) Assert.DoesNotContain(secret, html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_reversed_payment_prints_as_cancelled_without_saying_why()
    {
        var w = await NewWorld("receiptvoid");
        var pay = await Pay(w.Owner, w.TenantA, 777m, reference: "REF-ANULADO");
        var id = pay.GetProperty("id").GetGuid();
        await w.Owner.GetAsync($"/api/v1/payments/{id}/receipt.pdf");
        Assert.DoesNotContain("CANCELADO", fx.Pdf.LastHtmlWith("REF-ANULADO")!);

        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/payments/{id}/reverse", new { reason = "MOTIVO-SECRETO-DEL-REVERSO" })).StatusCode);
        Assert.True((await Json(await w.Owner.GetAsync($"/api/v1/payments/{id}/receipt"))).GetProperty("cancelled").GetBoolean());
        await w.Owner.GetAsync($"/api/v1/payments/{id}/receipt.pdf");
        var html = fx.Pdf.LastHtmlWith("REF-ANULADO")!;
        Assert.Contains("CANCELADO", html);
        Assert.Contains("R-000001", html);                              // the number is still there: it is never reused
        Assert.DoesNotContain("MOTIVO-SECRETO-DEL-REVERSO", html);
        Assert.Contains("porque el pago se revirtió", html);
        Assert.DoesNotContain("Saldo a favor de este pago", html);
    }

    [Fact]
    public async Task Everything_written_into_a_page_is_escaped()
    {
        var w = await NewWorld("receiptxss", tenantA: "<script>alert(1)</script> Eve & \"Co\"");
        var pay = await Pay(w.Owner, w.TenantA, 5m, reference: "<img src=x onerror=alert(2)>");
        await w.Owner.GetAsync($"/api/v1/payments/{pay.GetProperty("id").GetGuid()}/receipt.pdf");
        var html = fx.Pdf.LastHtmlWith("onerror")!;
        Assert.DoesNotContain("<script>", html); Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; Eve &amp; &quot;Co&quot;", html);
        Assert.Contains("&lt;img src=x onerror=alert(2)&gt;", html);
    }

    [Fact]
    public async Task Receipts_follow_the_roles_and_never_cross_tenants_or_organizations()
    {
        var w = await NewWorld("receiptroles");
        var (other, _, _) = await fx.RegisterAsync("receiptroles2");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector"); var finance = await fx.AddMemberAsync(w.Owner, "Finance"); var maintenance = await fx.AddMemberAsync(w.Owner, "Maintenance");
        var anas = (await Pay(w.Owner, w.TenantA, 100m, reference: "DE-ANA")).GetProperty("id").GetGuid();
        var lluis = (await Pay(w.Owner, w.TenantB, 200m, reference: "DE-LUIS")).GetProperty("id").GetGuid();

        foreach (var staff in new[] { w.Owner, finance, collector }) Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/payments/{anas}/receipt.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maintenance.GetAsync($"/api/v1/payments/{anas}/receipt.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/payments/{anas}/receipt.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/payments/{anas}/receipt")).StatusCode);

        // The tenant's own, through the portal. Another tenant's payment looks like it does not exist; the staff route is closed to tenants.
        Assert.Equal(HttpStatusCode.OK, (await w.A.GetAsync($"/api/v1/portal/payments/{anas}/receipt.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.A.GetAsync($"/api/v1/portal/payments/{lluis}/receipt.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.A.GetAsync($"/api/v1/portal/payments/{Guid.NewGuid()}/receipt.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.A.GetAsync($"/api/v1/payments/{anas}/receipt.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Owner.GetAsync($"/api/v1/portal/payments/{anas}/receipt.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collector.GetAsync($"/api/v1/portal/payments/{anas}/receipt.pdf")).StatusCode);

        var mine = (await Json(await w.A.GetAsync("/api/v1/portal/payments"))).GetProperty("items");
        Assert.Equal("R-000001", mine[0].GetProperty("receiptFolio").GetString());
    }

    // ---------- statement ----------

    [Fact]
    public async Task The_statement_lists_charges_and_payments_with_their_totals_and_leaves_out_voided_charges()
    {
        var w = await NewWorld("statement");
        var partly = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-10), "Renta parcial");
        await Charge(w.Owner, w.LeaseA, 500m, Today.AddDays(-2), "Renta sin pagar");
        var voided = await Charge(w.Owner, w.LeaseA, 300m, Today.AddDays(-2), "CARGO-ANULADO-X");
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/rent-charges/{voided}/void", new { reason = "Error" })).StatusCode);
        await Charge(w.Owner, w.LeaseB, 9999m, Today.AddDays(-2), "DE-LUIS-NO-DEBE-SALIR");
        var future = await Charge(w.Owner, w.LeaseA, 600m, Today.AddDays(20), "Renta futura");
        await Pay(w.Owner, w.TenantA, 400m, reference: "PAGO-PARCIAL", allocs: (partly, 400m));
        await Pay(w.Owner, w.TenantA, 200m, reference: "PAGO-FUTURO", allocs: (future, 200m));
        await Pay(w.Owner, w.TenantA, 250m, reference: "PAGO-SUELTO");                   // credit: not applied to any charge

        var r = await w.Owner.GetAsync($"/api/v1/tenants/{w.TenantA}/statement.pdf");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = fx.Pdf.LastHtmlWith("Renta parcial")!;
        Assert.Contains("Ana Ejemplo", html);
        Assert.Contains("Renta parcial", html); Assert.Contains("Renta sin pagar", html);
        Assert.DoesNotContain("CARGO-ANULADO-X", html); Assert.DoesNotContain("DE-LUIS-NO-DEBE-SALIR", html);
        Assert.Contains("R-000001", html); Assert.Contains("R-000002", html); Assert.Contains("R-000003", html);
        Assert.Contains("$2,100.00", html);            // charged: 1000 + 500 + 600
        Assert.Contains("$600.00", html);              // paid: only what was applied to charges (400 + 200)
        Assert.Contains("$1,500.00", html);            // balance
        Assert.Contains("$250.00", html);              // credit: the payment applied to nothing
        Assert.Contains("Pago parcial", html);         // the not-yet-due charge that was partly paid
        Assert.Contains("Vencido", html);              // past due wins over "partly paid", as everywhere else in Habita
    }

    [Fact]
    public async Task The_statement_is_for_staff_who_read_reports_and_for_the_tenant_themselves_only()
    {
        var w = await NewWorld("statementroles");
        var (other, _, _) = await fx.RegisterAsync("statementroles2");
        var finance = await fx.AddMemberAsync(w.Owner, "Finance"); var readOnly = await fx.AddMemberAsync(w.Owner, "ReadOnly");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector"); var maintenance = await fx.AddMemberAsync(w.Owner, "Maintenance");
        await Charge(w.Owner, w.LeaseA, 123m, Today.AddDays(-1), "CARGO-DE-ANA"); await Charge(w.Owner, w.LeaseB, 456m, Today.AddDays(-1), "CARGO-DE-LUIS");

        foreach (var staff in new[] { w.Owner, finance, readOnly }) Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/tenants/{w.TenantA}/statement.pdf")).StatusCode);
        foreach (var blocked in new[] { collector, maintenance, w.A, w.B }) Assert.Equal(HttpStatusCode.Forbidden, (await blocked.GetAsync($"/api/v1/tenants/{w.TenantA}/statement.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/tenants/{w.TenantA}/statement.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.GetAsync($"/api/v1/tenants/{Guid.NewGuid()}/statement.pdf")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await w.A.GetAsync("/api/v1/portal/statement.pdf")).StatusCode);
        var html = fx.Pdf.LastHtmlWith("CARGO-DE-ANA")!;
        Assert.DoesNotContain("CARGO-DE-LUIS", html);                      // there is no way to ask for Luis's: the portal route takes no id
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Owner.GetAsync("/api/v1/portal/statement.pdf")).StatusCode);
    }

    // ---------- the PDF service and its limits ----------

    [Fact]
    public async Task When_the_pdf_service_is_down_documents_answer_503_and_nothing_else_breaks()
    {
        var w = await NewWorld("pdfdown");
        var pay = (await Pay(w.Owner, w.TenantA, 10m)).GetProperty("id").GetGuid();
        fx.Pdf.Down = true;
        try
        {
            foreach (var url in new[] { $"/api/v1/payments/{pay}/receipt.pdf", $"/api/v1/tenants/{w.TenantA}/statement.pdf" })
            {
                var r = await w.Owner.GetAsync(url);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
                Assert.Contains("PDF", (await Json(r)).GetProperty("detail").GetString());
            }
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await w.A.GetAsync($"/api/v1/portal/payments/{pay}/receipt.pdf")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await w.Owner.GetAsync($"/api/v1/payments/{pay}/receipt")).StatusCode);   // the number and its data do not need the PDF service
            Assert.Equal("R-000002", (await Pay(w.Owner, w.TenantA, 20m)).GetProperty("receiptFolio").GetString());   // and recording payments keeps working
        }
        finally { fx.Pdf.Down = false; }
    }

    [Fact]
    public async Task Pdf_downloads_are_rate_limited_per_person()
    {
        var w = await NewWorld("pdfrate");
        var pay = (await Pay(w.Owner, w.TenantA, 10m)).GetProperty("id").GetGuid();
        // A signed-in client of a server with limits on (the shared fixture switches them off): log the same owner in again there.
        var ownerEmail = (await Json(await w.Owner.GetAsync("/api/v1/me"))).GetProperty("user").GetProperty("email").GetString();
        var limited = fx.Factory.WithWebHostBuilder(b => { b.UseSetting("RateLimiting:Enabled", "true"); b.UseSetting("RateLimiting:PdfPerMinute", "2"); }).CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await limited.PostAsJsonAsync("/api/v1/auth/login", new { email = ownerEmail, password = ApiFixture.Password })).StatusCode);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) statuses.Add((await limited.GetAsync($"/api/v1/payments/{pay}/receipt.pdf")).StatusCode);
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Document_routes_need_a_session()
    {
        var anon = fx.NewClient(); var id = Guid.NewGuid();
        foreach (var url in new[] { "/api/v1/contract-template", "/api/v1/contract-template/sample", $"/api/v1/leases/{id}/documents", $"/api/v1/payments/{id}/receipt.pdf",
                                    $"/api/v1/tenants/{id}/statement.pdf", "/api/v1/portal/statement.pdf", "/api/v1/portal/contract.pdf", $"/api/v1/portal/payments/{id}/receipt.pdf" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync($"/api/v1/leases/{id}/documents", null)).StatusCode);
    }
}
