using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Habita.Application.Abstractions;
using Habita.Application.Notifications;
using Habita.Domain.Organizations;
using Habita.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Habita.IntegrationTests;

/// <summary>A fake clock for reminders and a fake PDF service for contracts, on one API.</summary>
public class DataFixture : ApiFixture
{
    public FakeClock Clock { get; } = new();
    public FakePdf Pdf { get; } = new();
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Factory = Factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IClock>(); s.AddSingleton<IClock>(Clock);
            s.RemoveAll<IPdfConverter>(); s.AddSingleton<IPdfConverter>(Pdf);
        }));
    }
}

/// <summary>Hito 15: grace days, guarantor and inventory, and deposits held in custody.</summary>
public class Hito15Tests(DataFixture fx) : IClassFixture<DataFixture>
{
    static readonly TimeZoneInfo Mexico = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    static string D(DateOnly d) => d.ToString("yyyy-MM-dd");
    static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, Mexico).Date);
    static DateTimeOffset Local(DateOnly day, int hour, int minute = 0) => new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, minute)), Mexico), TimeSpan.Zero);

    // ---------- scaffolding ----------

    record World(HttpClient Owner, Guid OrgId, HttpClient A, HttpClient B, Guid TenantA, Guid TenantB, Guid LeaseA, Guid LeaseB, Guid PropertyId);

    int _unit;
    async Task<Guid> Lease(HttpClient owner, Guid propertyId, Guid tenantId, decimal? deposit = 8500m, object? extra = null)
    {
        var uid = (await Json(await owner.PostAsJsonAsync($"/api/v1/properties/{propertyId}/units", new { code = $"G{Interlocked.Increment(ref _unit)}", baseRent = 8500 }))).GetProperty("id").GetGuid();
        var r = await owner.PostAsJsonAsync("/api/v1/leases", new { unitId = uid, tenantId, startDate = D(Today.AddMonths(-6)), monthlyRent = 8500, dueDay = 5, depositAmount = deposit, termsNotes = "Sin prorrateo." });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var lid = (await Json(r)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/leases/{lid}/activate", null)).StatusCode);
        return lid;
    }

    async Task<HttpClient> Login(HttpClient owner, Guid tenantId)
    {
        var code = (await Json(await owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Tenant", tenantId }))).GetProperty("code").GetString();
        var c = fx.NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/v1/auth/access-codes/redeem", new { code, email = $"inq-{Guid.NewGuid():N}@example.com", fullName = "Inquilino", password = ApiFixture.Password })).StatusCode);
        return c;
    }

    async Task<World> NewWorld(string tag, decimal? deposit = 8500m)
    {
        fx.Clock.Now = DateTimeOffset.UtcNow; fx.Pdf.Down = false;
        var (owner, orgId, _) = await fx.RegisterAsync(tag);
        var pid = (await Json(await owner.PostAsJsonAsync("/api/v1/properties", new { name = "Torre Norte", address = "Av. Demo 123" }))).GetProperty("id").GetGuid();
        var ta = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Ana Ejemplo", phone = "55 1234 5678" }))).GetProperty("id").GetGuid();
        var tb = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Luis Prueba" }))).GetProperty("id").GetGuid();
        var la = await Lease(owner, pid, ta, deposit); var lb = await Lease(owner, pid, tb, deposit);
        return new(owner, orgId, await Login(owner, ta), await Login(owner, tb), ta, tb, la, lb, pid);
    }

    static async Task<Guid> Charge(HttpClient c, Guid leaseId, decimal amount, DateOnly due, string description = "Renta")
    {
        var r = await c.PostAsJsonAsync("/api/v1/rent-charges", new { leaseId, description, amount, dueDate = D(due) });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    static async Task<JsonElement> Pay(HttpClient c, Guid tenantId, decimal amount, params (Guid Charge, decimal Amount)[] allocs)
    {
        var r = await c.PostAsJsonAsync("/api/v1/payments", new { tenantId, receivedOn = D(Today), amount, method = "BankTransfer", allocations = allocs.Select(a => new { chargeId = a.Charge, amount = a.Amount }).ToArray() });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Json(r);
    }

    async Task Sql(string sql, params object[] args)
    {
        using var scope = fx.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<HabitaDbContext>().Database.ExecuteSqlRawAsync(sql, args);
    }

    Task<ReminderRun> RemindersFor(Guid orgId) => InOrg(orgId, sp => sp.GetRequiredService<ReminderService>().RunIfDueAsync(default));
    async Task<T> InOrg<T>(Guid orgId, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = fx.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentOrganization>().Set(orgId, Role.Owner, OrganizationStatus.Active);
        return await work(scope.ServiceProvider);
    }
    static async Task<List<string>> Titles(HttpClient c) =>
        (await Json(await c.GetAsync("/api/v1/notifications?pageSize=50"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).OrderBy(t => t).ToList();

    static async Task<JsonElement> Summary(HttpClient c) =>
        await Json(await c.GetAsync($"/api/v1/reports/financial-summary?from={D(Today.AddMonths(-1))}&to={D(Today.AddMonths(1))}"));

    // ---------- grace days ----------

    [Fact]
    public async Task Grace_days_default_to_zero_are_limited_and_audited()
    {
        var w = await NewWorld("graceset");
        Assert.Equal(0, (await Json(await w.Owner.GetAsync("/api/v1/settings"))).GetProperty("graceDays").GetInt32());
        foreach (var bad in new[] { -1, 31, 100 }) Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PatchAsJsonAsync("/api/v1/settings", new { graceDays = bad })).StatusCode);
        Assert.Equal(3, (await Json(await w.Owner.PatchAsJsonAsync("/api/v1/settings", new { graceDays = 3 }))).GetProperty("graceDays").GetInt32());
        Assert.Equal(30, (await Json(await w.Owner.PatchAsJsonAsync("/api/v1/settings", new { graceDays = 30 }))).GetProperty("graceDays").GetInt32());
        Assert.Contains("graceDays", await (await w.Owner.GetAsync("/api/v1/audit-logs?entityType=OrganizationSettings")).Content.ReadAsStringAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"Settings\" SET \"GraceDays\" = 31 WHERE \"OrganizationId\" = {0}", w.OrgId)); // and the database refuses it too
    }

    [Fact]
    public async Task Grace_days_move_the_line_between_pending_and_overdue_everywhere()
    {
        var w = await NewWorld("grace");
        foreach (var daysLate in new[] { 2, 3, 4, 10 }) await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-daysLate), $"Atraso {daysLate}");
        await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(5), "Futuro");
        var finance = await fx.AddMemberAsync(w.Owner, "Finance");

        async Task<(int overdueList, int pendingList, int summary, int portal, int balances)> Snapshot()
        {
            var overdue = (await Json(await w.Owner.GetAsync("/api/v1/rent-charges?status=Overdue&pageSize=50"))).GetProperty("total").GetInt32();
            var pending = (await Json(await w.Owner.GetAsync("/api/v1/rent-charges?status=Open&pageSize=50"))).GetProperty("total").GetInt32();
            var summary = (await Summary(w.Owner)).GetProperty("overdueCharges").GetInt32();
            var portal = (await Json(await w.A.GetAsync("/api/v1/portal/summary"))).GetProperty("overdueCount").GetInt32();
            var csv = await (await finance.GetAsync("/api/v1/reports/balances.csv")).Content.ReadAsStringAsync();
            return (overdue, pending, summary, portal, csv.Split('\n').Count(l => l.Contains("Vencido")));
        }

        Assert.Equal((4, 1, 4, 4, 4), await Snapshot());                    // no grace: everything past its due day is overdue (as before)
        await w.Owner.PatchAsJsonAsync("/api/v1/settings", new { graceDays = 3 });
        // With 3 grace days a charge 2 or 3 days late is still pending, 4 days late is overdue.
        Assert.Equal((2, 3, 2, 2, 2), await Snapshot());
        await w.Owner.PatchAsJsonAsync("/api/v1/settings", new { graceDays = 30 });
        Assert.Equal((0, 5, 0, 0, 0), await Snapshot());
        await w.Owner.PatchAsJsonAsync("/api/v1/settings", new { graceDays = 0 });
        Assert.Equal((4, 1, 4, 4, 4), await Snapshot());
    }

    [Fact]
    public async Task The_charge_itself_the_statement_and_the_cash_screen_show_the_same_status()
    {
        var w = await NewWorld("graceview");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector");
        var chargeId = await Charge(w.Owner, w.LeaseA, 500m, Today.AddDays(-2), "En gracia");
        await w.Owner.PatchAsJsonAsync("/api/v1/settings", new { graceDays = 5 });
        Assert.Equal("Open", (await Json(await w.Owner.GetAsync($"/api/v1/rent-charges/{chargeId}"))).GetProperty("status").GetString());
        Assert.Equal("Open", (await Json(await w.A.GetAsync("/api/v1/portal/charges"))).GetProperty("items")[0].GetProperty("status").GetString());

        var token = await Json(await w.A.PostAsync("/api/v1/portal/cash-token", null));
        var info = await Json(await collector.PostAsJsonAsync("/api/v1/cash-tokens/resolve", new { code = token.GetProperty("code").GetString() }));
        Assert.Equal("Open", info.GetProperty("charges")[0].GetProperty("status").GetString());
        await w.Owner.GetAsync($"/api/v1/tenants/{w.TenantA}/statement.pdf");
        var html = fx.Pdf.Html.Reverse().First(h => h.Contains("En gracia"));
        Assert.Contains("Por pagar", html); Assert.DoesNotContain("Vencido", html);

        await w.Owner.PatchAsJsonAsync("/api/v1/settings", new { graceDays = 0 });
        Assert.Equal("Overdue", (await Json(await w.Owner.GetAsync($"/api/v1/rent-charges/{chargeId}"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Overdue_notices_count_from_the_end_of_the_grace_period()
    {
        var w = await NewWorld("gracenotice");
        await w.Owner.PatchAsJsonAsync("/api/v1/settings", new { graceDays = 3 });
        await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-1), "Un día tarde");       // inside the grace period: no notice yet
        await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-4), "Cuatro días tarde");  // first day overdue with 3 grace days
        await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-8), "Ocho días tarde");    // 3 + 7 = 10 would be the next step: not today
        fx.Clock.Now = Local(Today, 9, 30);
        Assert.True((await RemindersFor(w.OrgId)).Ran);
        var mine = await Titles(w.A);
        Assert.Single(mine);
        var body = (await Json(await w.A.GetAsync("/api/v1/notifications?pageSize=10"))).GetProperty("items")[0].GetProperty("body").GetString();
        Assert.Contains("Cuatro días tarde", body); Assert.Contains("venció hace 4 días", body);
    }

    // ---------- guarantor ----------

    [Fact]
    public async Task A_lease_can_carry_a_guarantor_that_can_be_changed_and_cleared()
    {
        var w = await NewWorld("guarantor");
        var tenant = (await Json(await w.Owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Carla Sánchez" }))).GetProperty("id").GetGuid();
        var uid = (await Json(await w.Owner.PostAsJsonAsync($"/api/v1/properties/{w.PropertyId}/units", new { code = "FIA1", baseRent = 100 }))).GetProperty("id").GetGuid();
        var create = await w.Owner.PostAsJsonAsync("/api/v1/leases", new { unitId = uid, tenantId = tenant, startDate = D(Today), monthlyRent = 100, dueDay = 5, guarantorName = "  Pedro Fiador ", guarantorPhone = "55 8765 4321" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var lease = await Json(create); var id = lease.GetProperty("id").GetGuid();
        Assert.Equal("Pedro Fiador", lease.GetProperty("guarantorName").GetString()); Assert.Equal("55 8765 4321", lease.GetProperty("guarantorPhone").GetString());
        await w.Owner.PostAsync($"/api/v1/leases/{id}/activate", null);

        // An active lease still lets the guarantor change (it is not a structural change).
        var changed = await Json(await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{id}", new { guarantorName = "Pedro Fiador López", guarantorPhone = "55 1111 2222" }));
        Assert.Equal("Pedro Fiador López", changed.GetProperty("guarantorName").GetString());
        var untouched = await Json(await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{id}", new { monthlyRent = 150 }));
        Assert.Equal("Pedro Fiador López", untouched.GetProperty("guarantorName").GetString());   // absent field = unchanged
        var cleared = await Json(await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{id}", new { guarantorName = "" }));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("guarantorName").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("guarantorPhone").ValueKind);        // a phone never stays without its name

        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{id}", new { guarantorPhone = "55 1111 2222" })).StatusCode); // phone with no name
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{id}", new { guarantorName = "Pedro", guarantorPhone = "abc" })).StatusCode);
        var audit = await (await w.Owner.GetAsync("/api/v1/audit-logs?entityType=Lease")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Pedro", audit); Assert.DoesNotContain("1111", audit);              // the audit records that it changed, never to whom
    }

    // ---------- inventory ----------

    static object Item(string name, int qty = 1, string cond = "Good", string? notes = null, Guid? id = null) => new { id, name, quantity = qty, condition = cond, notes };

    [Fact]
    public async Task The_inventory_is_replaced_as_a_whole_keeps_existing_items_and_respects_the_rules()
    {
        var w = await NewWorld("inventory");
        var path = $"/api/v1/leases/{w.LeaseA}/inventory";
        Assert.Empty((await Json(await w.Owner.GetAsync(path))).EnumerateArray());

        var first = await Json(await w.Owner.PutAsJsonAsync(path, new { items = new[] { Item("Estufa", 1, "Good"), Item("Cama", 2, "Fair", "Colchón con una mancha"), Item("Ventana de la sala", 1, "Poor") } }));
        Assert.Equal(new[] { "Estufa", "Cama", "Ventana de la sala" }, first.EnumerateArray().Select(i => i.GetProperty("name").GetString()));
        var cama = first[1].GetProperty("id").GetGuid(); var estufa = first[0].GetProperty("id").GetGuid();
        Assert.Equal("Fair", first[1].GetProperty("condition").GetString()); Assert.Equal(2, first[1].GetProperty("quantity").GetInt32());

        // Keep the stove (by id), drop the bed, add a fridge, put the fridge first: ids of kept items survive.
        var second = await Json(await w.Owner.PutAsJsonAsync(path, new { items = new[] { Item("Refrigerador", 1, "Good"), Item("Estufa", 1, "Fair", id: estufa) } }));
        Assert.Equal(new[] { "Refrigerador", "Estufa" }, second.EnumerateArray().Select(i => i.GetProperty("name").GetString()));
        Assert.Equal(estufa, second[1].GetProperty("id").GetGuid()); Assert.Equal("Fair", second[1].GetProperty("condition").GetString());
        Assert.DoesNotContain(second.EnumerateArray(), i => i.GetProperty("id").GetGuid() == cama);

        foreach (var bad in new object[]
        {
            new { items = new[] { Item("") } }, new { items = new[] { Item("Cama", 0) } }, new { items = new[] { Item("Cama", 1000) } }, new { items = new[] { Item("Cama", 1, "Roto") } },
            new { items = new[] { Item(new string('x', 101)) } }, new { items = new[] { Item("Cama", 1, "Good", new string('n', 301)) } },
            new { items = Enumerable.Range(0, 101).Select(i => Item($"Cosa {i}")).ToArray() },
            new { items = new[] { Item("A", id: Guid.NewGuid()) } },                                          // an id that is not this lease's
            new { items = new[] { Item("A", id: estufa), Item("B", id: estufa) } },                           // the same id twice
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PutAsJsonAsync(path, bad)).StatusCode);
        Assert.Equal(2, (await Json(await w.Owner.GetAsync(path))).GetArrayLength());                          // the failed attempts changed nothing
        Assert.Empty((await Json(await w.Owner.PutAsJsonAsync(path, new { items = Array.Empty<object>() }))).EnumerateArray()); // an empty list clears it
    }

    [Fact]
    public async Task Replacing_the_inventory_from_several_screens_at_once_never_duplicates_items()
    {
        var w = await NewWorld("inventoryrace");
        var path = $"/api/v1/leases/{w.LeaseA}/inventory";
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => w.Owner.PutAsJsonAsync(path, new { items = new[] { Item("Estufa"), Item("Cama"), Item("Mesa") } })));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(3, (await Json(await w.Owner.GetAsync(path))).GetArrayLength());
    }

    [Fact]
    public async Task The_return_condition_is_recorded_after_the_lease_ends_next_to_the_delivery_condition()
    {
        var w = await NewWorld("inventoryreturn");
        var path = $"/api/v1/leases/{w.LeaseA}/inventory";
        var items = await Json(await w.Owner.PutAsJsonAsync(path, new { items = new[] { Item("Estufa", 1, "Good"), Item("Cama", 1, "Good") } }));
        var estufa = items[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"{path}/return", new { items = new[] { new { itemId = estufa, condition = "Poor" } } })).StatusCode); // still active

        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseA}/terminate", new { endDate = D(Today), reason = "Fin de contrato" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PutAsJsonAsync(path, new { items = new[] { Item("Otra cosa") } })).StatusCode);   // the delivery list is closed
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"{path}/return", new { items = new[] { new { itemId = Guid.NewGuid(), condition = "Poor" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"{path}/return", new { items = new[] { new { itemId = estufa, condition = "Roto" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"{path}/return", new { items = Array.Empty<object>() })).StatusCode);

        var back = await Json(await w.Owner.PostAsJsonAsync($"{path}/return", new { items = new[] { new { itemId = estufa, condition = "Poor", notes = "Quemador dañado" } } }));
        Assert.Equal("Good", back[0].GetProperty("condition").GetString()); Assert.Equal("Poor", back[0].GetProperty("returnCondition").GetString());
        Assert.Equal("Quemador dañado", back[0].GetProperty("returnNotes").GetString());
        Assert.Equal(JsonValueKind.Null, back[1].GetProperty("returnCondition").ValueKind);        // the bed was not touched
    }

    [Fact]
    public async Task Inventory_and_deposit_follow_the_roles_and_the_organization()
    {
        var w = await NewWorld("extrasroles");
        var (other, _, _) = await fx.RegisterAsync("extrasroles2");
        var admin = await fx.AddMemberAsync(w.Owner, "Admin"); var finance = await fx.AddMemberAsync(w.Owner, "Finance"); var readOnly = await fx.AddMemberAsync(w.Owner, "ReadOnly");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector"); var maintenance = await fx.AddMemberAsync(w.Owner, "Maintenance");
        var inv = $"/api/v1/leases/{w.LeaseA}/inventory"; var dep = $"/api/v1/leases/{w.LeaseA}/deposit";

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(inv, new { items = new[] { Item("Estufa") } })).StatusCode);
        foreach (var reader in new[] { finance, readOnly }) { Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(inv)).StatusCode); Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(dep)).StatusCode); }
        foreach (var blocked in new[] { collector, maintenance, w.A, w.B })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await blocked.GetAsync(inv)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await blocked.GetAsync(dep)).StatusCode);
        }
        foreach (var cannot in new[] { finance, readOnly, collector, maintenance, w.A })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cannot.PutAsJsonAsync(inv, new { items = new[] { Item("X") } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cannot.PostAsJsonAsync($"{dep}/schedule", new { installments = new[] { new { dueDate = D(Today), amount = 8500m } } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cannot.PostAsJsonAsync($"{dep}/settle", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cannot.PostAsJsonAsync($"{dep}/settlement/void", new { reason = "x" })).StatusCode);
        }
        foreach (var url in new[] { inv, dep }) Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(inv, new { items = new[] { Item("X") } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{dep}/settle", new { })).StatusCode);
    }

    // ---------- contract fields ----------

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

    async Task<string> GenerateContractText(World w, Guid leaseId, params string[] template)
    {
        var part = new ByteArrayContent(Docx(template)); part.Headers.ContentType = new MediaTypeHeaderValue(Habita.Domain.Documents.ContractTemplate.DocxContentType);
        part.Headers.TryAddWithoutValidation("Content-Disposition", "form-data; name=\"file\"; filename=\"p.docx\"");
        Assert.Equal(HttpStatusCode.Created, (await w.Owner.PostAsync("/api/v1/contract-template", new MultipartFormDataContent { part })).StatusCode);
        var doc = await Json(await w.Owner.PostAsync($"/api/v1/leases/{leaseId}/documents", null));
        return DocxText(await (await w.Owner.GetAsync($"/api/v1/leases/{leaseId}/documents/{doc.GetProperty("id").GetGuid()}/docx")).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_contract_fills_guarantor_inventory_and_deposit_and_drops_the_lines_that_stay_empty()
    {
        var w = await NewWorld("contractdata");
        string[] template = ["Fiador: {{fiador_nombre}} / {{fiador_telefono}}", "{{fiador_linea}}", "Inventario:", "{{inventario}}", "Depósito {{deposito}} {{deposito_tipo}}: {{deposito_fechas}}"];

        // Nothing set yet: no guarantor line, an empty inventory, and a deposit that has not been scheduled.
        var empty = await GenerateContractText(w, w.LeaseA, template);
        Assert.DoesNotContain("FIADOR", empty);
        Assert.Contains("Depósito $8,500.00 : ", empty);

        await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{w.LeaseA}", new { guarantorName = "Pedro Fiador", guarantorPhone = "55 8765 4321" });
        await w.Owner.PutAsJsonAsync($"/api/v1/leases/{w.LeaseA}/inventory", new { items = new[] { Item("Estufa", 1, "Good"), Item("Cama", 2, "Fair", "Colchón manchado") } });
        await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseA}/deposit/schedule", new { installments = new[] { new { dueDate = "2026-11-05", amount = 4000m }, new { dueDate = "2026-10-05", amount = 4500m } } });
        var full = await GenerateContractText(w, w.LeaseA, template);
        Assert.Contains("Fiador: Pedro Fiador / 55 8765 4321", full);
        Assert.Contains("FIADOR: Pedro Fiador — Tel: 55 8765 4321", full);
        Assert.Contains("1 × Estufa — Bueno", full);
        Assert.Contains("2 × Cama — Regular (Colchón manchado)", full);
        Assert.Contains("en 2 abonos: 5 de octubre de 2026 ($4,500.00), 5 de noviembre de 2026 ($4,000.00)", full);   // dates in order, whatever order they were typed
    }

    // ---------- deposits ----------

    static object Installments(params (string Due, decimal Amount)[] i) => new { installments = i.Select(x => new { dueDate = x.Due, amount = x.Amount }).ToArray() };
    static async Task<JsonElement> Deposit(HttpClient c, Guid lease) => await Json(await c.GetAsync($"/api/v1/leases/{lease}/deposit"));

    [Fact]
    public async Task A_deposit_is_scheduled_in_installments_that_add_up_exactly_to_what_the_contract_says()
    {
        var w = await NewWorld("depsched");
        var path = $"/api/v1/leases/{w.LeaseA}/deposit";
        var before = await Deposit(w.Owner, w.LeaseA);
        Assert.Equal(8500m, before.GetProperty("depositAmount").GetDecimal()); Assert.Equal(8500m, before.GetProperty("unscheduled").GetDecimal()); Assert.Equal(0, before.GetProperty("installments").GetArrayLength());

        foreach (var bad in new object[]
        {
            Installments(), Installments(("2026-11-01", 8000m)), Installments(("2026-11-01", 9000m)), Installments(("2026-11-01", 4250.5m), ("2026-12-01", 4250m)),
            Installments(("2026-11-01", 0m), ("2026-12-01", 8500m)), Installments(("1999-01-01", 8500m)), Installments(("2026-11-01", 8500.005m)),
            new { installments = Enumerable.Range(0, 13).Select(i => new { dueDate = "2026-11-01", amount = 100m }).ToArray() },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"{path}/schedule", bad)).StatusCode);
        Assert.Equal(0, (await Deposit(w.Owner, w.LeaseA)).GetProperty("installments").GetArrayLength());   // the refused attempts left nothing behind

        var ok = await w.Owner.PostAsJsonAsync($"{path}/schedule", Installments(("2026-12-01", 2500m), ("2026-10-01", 3000m), ("2026-11-01", 3000m)));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var d = await Json(ok);
        Assert.Equal(new[] { "Depósito (abono 1 de 3)", "Depósito (abono 2 de 3)", "Depósito (abono 3 de 3)" }, d.GetProperty("installments").EnumerateArray().Select(i => i.GetProperty("description").GetString()));
        Assert.Equal(new[] { "2026-10-01", "2026-11-01", "2026-12-01" }, d.GetProperty("installments").EnumerateArray().Select(i => i.GetProperty("dueDate").GetString()));   // numbered by date
        Assert.Equal(8500m, d.GetProperty("scheduled").GetDecimal()); Assert.Equal(0m, d.GetProperty("unscheduled").GetDecimal()); Assert.Equal(0m, d.GetProperty("custody").GetDecimal());

        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"{path}/schedule", Installments(("2027-01-01", 100m)))).StatusCode); // nothing left to schedule
        var charges = (await Json(await w.Owner.GetAsync($"/api/v1/rent-charges?leaseId={w.LeaseA}&pageSize=50"))).GetProperty("items").EnumerateArray().Where(c => c.GetProperty("type").GetString() == "Deposit").ToList();
        Assert.Equal(3, charges.Count);
        Assert.Contains(await Json(await w.A.GetAsync("/api/v1/portal/charges?pageSize=50")) is var portal ? portal.GetProperty("items").EnumerateArray() : [], c => c.GetProperty("description").GetString()!.StartsWith("Depósito (abono 1"));
    }

    [Fact]
    public async Task Voiding_an_installment_frees_its_amount_to_be_scheduled_again()
    {
        var w = await NewWorld("depvoid");
        var path = $"/api/v1/leases/{w.LeaseA}/deposit";
        var d = await Json(await w.Owner.PostAsJsonAsync($"{path}/schedule", Installments(("2026-11-01", 4000m), ("2026-12-01", 4500m))));
        var second = d.GetProperty("installments")[1].GetProperty("chargeId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/rent-charges/{second}/void", new { reason = "Cambio de fecha" })).StatusCode);
        var after = await Deposit(w.Owner, w.LeaseA);
        Assert.Equal(4500m, after.GetProperty("unscheduled").GetDecimal()); Assert.Equal(1, after.GetProperty("installments").GetArrayLength());
        var again = await Json(await w.Owner.PostAsJsonAsync($"{path}/schedule", Installments(("2027-01-01", 2000m), ("2027-02-01", 2500m))));
        Assert.Equal(3, again.GetProperty("installments").GetArrayLength());
        Assert.Equal("Depósito (abono 2 de 3)", again.GetProperty("installments")[1].GetProperty("description").GetString());   // numbering continues; the voided one is not counted
    }

    [Fact]
    public async Task A_lease_without_deposit_or_not_in_force_cannot_schedule_one_and_two_people_cannot_both_fill_it()
    {
        var w = await NewWorld("depguard");
        var none = await Lease(w.Owner, w.PropertyId, (await Json(await w.Owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Sin Depósito" }))).GetProperty("id").GetGuid(), deposit: null);
        var r = await w.Owner.PostAsJsonAsync($"/api/v1/leases/{none}/deposit/schedule", Installments(("2026-11-01", 100m)));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); Assert.Contains("no tiene un depósito", (await Json(r)).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{Guid.NewGuid()}/deposit/schedule", Installments(("2026-11-01", 100m)))).StatusCode);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseB}/deposit/schedule", Installments(("2026-11-01", 4250m), ("2026-12-01", 4250m)))));
        Assert.Equal(1, results.Count(x => x.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(x => x.StatusCode == HttpStatusCode.BadRequest));
        Assert.Equal(2, (await Deposit(w.Owner, w.LeaseB)).GetProperty("installments").GetArrayLength());     // exactly one schedule won

        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseB}/terminate", new { endDate = D(Today), reason = "Fin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseB}/deposit/schedule", Installments(("2027-01-01", 100m)))).StatusCode);
    }

    [Fact]
    public async Task Money_applied_to_a_deposit_is_held_in_custody_and_never_counted_as_income()
    {
        var w = await NewWorld("depincome");
        var before = await Summary(w.Owner);       // measured before anything is scheduled or charged
        var d = await Json(await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseA}/deposit/schedule", Installments((D(Today), 8500m))));
        var installment = d.GetProperty("installments")[0].GetProperty("chargeId").GetGuid();
        var rent = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-1));
        var pay = await Pay(w.Owner, w.TenantA, 6000m, (installment, 5000m), (rent, 1000m));
        var after = await Summary(w.Owner);
        Assert.Equal(1000m, after.GetProperty("paymentsReceived").GetProperty("total").GetDecimal() - before.GetProperty("paymentsReceived").GetProperty("total").GetDecimal()); // only the rent part is income
        Assert.Equal(5000m, after.GetProperty("depositsReceived").GetDecimal());
        Assert.Equal(5000m, after.GetProperty("depositsHeld").GetDecimal());
        // The manual 1000 charge is "other"; the 8500 installment is neither rent nor "other", so only the manual one shows up here.
        Assert.Equal(1000m, after.GetProperty("otherChargesGenerated").GetProperty("total").GetDecimal() - before.GetProperty("otherChargesGenerated").GetProperty("total").GetDecimal());
        Assert.Equal(0m, after.GetProperty("rentChargesGenerated").GetProperty("total").GetDecimal() - before.GetProperty("rentChargesGenerated").GetProperty("total").GetDecimal());
        var thisMonth = after.GetProperty("byMonth").EnumerateArray().First(m => m.GetProperty("month").GetInt32() == Today.Month && m.GetProperty("year").GetInt32() == Today.Year);
        Assert.Equal(1000m, thisMonth.GetProperty("received").GetDecimal());

        // What is still owed includes the installment (it IS owed), but it is not rent.
        Assert.Equal(3500m, (await Deposit(w.Owner, w.LeaseA)).GetProperty("outstanding").GetDecimal());
        var dash = await Json(await w.Owner.GetAsync("/api/v1/dashboard"));
        Assert.Equal(5000m, dash.GetProperty("finance").GetProperty("depositsHeld").GetDecimal());

        // Money that is not applied to anything is still income, as before.
        var loose = await Summary(w.Owner);
        await Pay(w.Owner, w.TenantB, 700m);
        Assert.Equal(700m, (await Summary(w.Owner)).GetProperty("paymentsReceived").GetProperty("total").GetDecimal() - loose.GetProperty("paymentsReceived").GetProperty("total").GetDecimal());

        // Reversing the payment gives the custody back: nothing is held and the income disappears from "received" as any reversal does.
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/payments/{pay.GetProperty("id").GetGuid()}/reverse", new { reason = "Devuelto" })).StatusCode);
        var reversed = await Summary(w.Owner);
        Assert.Equal(0m, reversed.GetProperty("depositsHeld").GetDecimal()); Assert.Equal(0m, reversed.GetProperty("depositsReceived").GetDecimal());
    }

    [Fact]
    public async Task The_charges_export_names_the_deposit_installments()
    {
        var w = await NewWorld("depcsv");
        await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseA}/deposit/schedule", Installments((D(Today), 8500m)));
        var csv = await (await w.Owner.GetAsync($"/api/v1/reports/charges.csv?from={D(Today.AddMonths(-1))}&to={D(Today.AddMonths(1))}")).Content.ReadAsStringAsync();
        Assert.Contains("Depósito", csv);
        Assert.Contains(csv.Split('\n'), line => line.Contains("Depósito") && line.Contains("8500"));
    }

    async Task<Guid> PaidDepositLease(World w, Guid lease, params (string Due, decimal Amount)[] schedule)
    {
        var d = await Json(await w.Owner.PostAsJsonAsync($"/api/v1/leases/{lease}/deposit/schedule", Installments(schedule)));
        foreach (var i in d.GetProperty("installments").EnumerateArray())
            await Pay(w.Owner, lease == w.LeaseA ? w.TenantA : w.TenantB, i.GetProperty("amount").GetDecimal(), (i.GetProperty("chargeId").GetGuid(), i.GetProperty("amount").GetDecimal()));
        return lease;
    }

    [Fact]
    public async Task A_deposit_is_settled_when_the_lease_is_over_with_every_deduction_and_its_reason()
    {
        var w = await NewWorld("depsettle");
        var path = $"/api/v1/leases/{w.LeaseA}/deposit";
        await PaidDepositLease(w, w.LeaseA, (D(Today.AddDays(-30)), 4000m), (D(Today.AddDays(-1)), 4500m));

        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"{path}/settle", new { })).StatusCode);                 // the lease is still in force
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseA}/terminate", new { endDate = D(Today), reason = "Fin de contrato" })).StatusCode);
        Assert.Equal(8500m, (await Deposit(w.Owner, w.LeaseA)).GetProperty("custody").GetDecimal());

        foreach (var bad in new object[]
        {
            new { deductions = new[] { new { concept = "Demasiado", amount = 8500.01m } } }, new { deductions = new[] { new { concept = "", amount = 100m } } },
            new { deductions = new[] { new { concept = "   ", amount = 100m } } }, new { deductions = new[] { new { concept = "A", amount = 0m } } },
            new { deductions = new[] { new { concept = "A", amount = 5000m }, new { concept = "B", amount = 4000m } } },
            new { settledOn = D(Today.AddDays(1)) }, new { notes = new string('n', 1001) },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"{path}/settle", bad)).StatusCode);
        Assert.Null((await Deposit(w.Owner, w.LeaseA)).GetProperty("settlement") is { ValueKind: not JsonValueKind.Null } s ? s : (JsonElement?)null); // none of those left a settlement

        var ok = await w.Owner.PostAsJsonAsync($"{path}/settle", new { deductions = new[] { new { concept = "Pintura de la sala", amount = 1200m }, new { concept = "Llave perdida", amount = 300m } }, notes = "Entregó llaves el último día." });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var settled = await Json(ok);
        var s1 = settled.GetProperty("settlement");
        Assert.Equal(8500m, s1.GetProperty("held").GetDecimal()); Assert.Equal(1500m, s1.GetProperty("deductionsTotal").GetDecimal()); Assert.Equal(7000m, s1.GetProperty("refund").GetDecimal());
        Assert.Equal(new[] { "Pintura de la sala", "Llave perdida" }, s1.GetProperty("deductions").EnumerateArray().Select(x => x.GetProperty("concept").GetString()));
        Assert.Equal(0m, settled.GetProperty("custody").GetDecimal());                                    // nothing is held any more

        var summary = await Summary(w.Owner);
        Assert.Equal(7000m, summary.GetProperty("depositsReturned").GetDecimal()); Assert.Equal(1500m, summary.GetProperty("depositsRetained").GetDecimal());
        Assert.Equal(0m, summary.GetProperty("depositsHeld").GetDecimal());

        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"{path}/settle", new { })).StatusCode);                  // only one live settlement per lease
        var audit = await (await w.Owner.GetAsync("/api/v1/audit-logs?action=deposit.settled")).Content.ReadAsStringAsync();
        Assert.Contains("deposit.settled", audit); Assert.DoesNotContain("Pintura", audit); Assert.DoesNotContain("Llave perdida", audit);   // amounts, not the reasons
    }

    [Fact]
    public async Task A_settlement_is_voided_with_a_reason_and_can_then_be_made_again()
    {
        var w = await NewWorld("depvoidsettle");
        var path = $"/api/v1/leases/{w.LeaseA}/deposit";
        await PaidDepositLease(w, w.LeaseA, (D(Today.AddDays(-1)), 8500m));
        await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseA}/terminate", new { endDate = D(Today), reason = "Fin" });
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"{path}/settlement/void", new { reason = "Nada que anular" })).StatusCode);
        await w.Owner.PostAsJsonAsync($"{path}/settle", new { deductions = new[] { new { concept = "Error de captura", amount = 8000m } } });

        foreach (var bad in new object[] { new { }, new { reason = "  " }, new { reason = new string('x', 1001) } })
            Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"{path}/settlement/void", bad)).StatusCode);
        var voided = await Json(await w.Owner.PostAsJsonAsync($"{path}/settlement/void", new { reason = "Las deducciones estaban mal" }));
        Assert.Equal(JsonValueKind.Null, voided.GetProperty("settlement").ValueKind);
        Assert.Equal(8500m, voided.GetProperty("custody").GetDecimal());                              // the money is held again
        var old = voided.GetProperty("voidedSettlements")[0];
        Assert.Equal("Las deducciones estaban mal", old.GetProperty("voidReason").GetString()); Assert.Equal(8000m, old.GetProperty("deductionsTotal").GetDecimal());

        var again = await Json(await w.Owner.PostAsJsonAsync($"{path}/settle", new { }));
        Assert.Equal(8500m, again.GetProperty("settlement").GetProperty("refund").GetDecimal());
        Assert.Equal(1, again.GetProperty("voidedSettlements").GetArrayLength());
    }

    [Fact]
    public async Task A_settlement_needs_everything_collected_or_voided_first_and_something_to_settle()
    {
        var w = await NewWorld("depsettleguard");
        var path = $"/api/v1/leases/{w.LeaseA}/deposit";
        var d = await Json(await w.Owner.PostAsJsonAsync($"{path}/schedule", Installments((D(Today.AddDays(-5)), 4000m), (D(Today.AddDays(30)), 4500m))));
        var first = d.GetProperty("installments")[0].GetProperty("chargeId").GetGuid(); var second = d.GetProperty("installments")[1].GetProperty("chargeId").GetGuid();
        await Pay(w.Owner, w.TenantA, 4000m, (first, 4000m));
        await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseA}/terminate", new { endDate = D(Today), reason = "Fin" });

        var pending = await w.Owner.PostAsJsonAsync($"{path}/settle", new { });
        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode); Assert.Contains("sin cobrar", (await Json(pending)).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/rent-charges/{second}/void", new { reason = "Se canceló el segundo abono" })).StatusCode);
        var ok = await Json(await w.Owner.PostAsJsonAsync($"{path}/settle", new { }));
        Assert.Equal(4000m, ok.GetProperty("settlement").GetProperty("held").GetDecimal());              // only what was really collected

        // A lease whose deposit was never collected has nothing to settle.
        await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseB}/terminate", new { endDate = D(Today), reason = "Fin" });
        var nothing = await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseB}/deposit/settle", new { });
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode); Assert.Contains("No hay depósito cobrado", (await Json(nothing)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_cancelled_lease_whose_deposit_was_paid_can_still_give_it_back()
    {
        var w = await NewWorld("depcancel");
        await PaidDepositLease(w, w.LeaseB, (D(Today.AddDays(-1)), 8500m));
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseB}/cancel", new { reason = "Se arrepintió" })).StatusCode);
        var settled = await Json(await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseB}/deposit/settle", new { }));
        Assert.Equal(8500m, settled.GetProperty("settlement").GetProperty("refund").GetDecimal());
    }

    [Fact]
    public async Task Settlements_started_together_produce_one()
    {
        var w = await NewWorld("depsettlerace");
        await PaidDepositLease(w, w.LeaseA, (D(Today.AddDays(-1)), 8500m));
        await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseA}/terminate", new { endDate = D(Today), reason = "Fin" });
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseA}/deposit/settle", new { })));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task A_settlement_and_its_deductions_cannot_be_changed_or_erased_in_the_database()
    {
        var w = await NewWorld("depimmut");
        await PaidDepositLease(w, w.LeaseA, (D(Today.AddDays(-1)), 8500m));
        await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseA}/terminate", new { endDate = D(Today), reason = "Fin" });
        await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseA}/deposit/settle", new { deductions = new[] { new { concept = "Pintura", amount = 500m } } });

        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"DepositSettlements\" SET \"RefundAmount\" = 8500, \"DeductionsTotal\" = 0 WHERE \"LeaseId\" = {0}", w.LeaseA));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"DepositSettlements\" SET \"Notes\" = 'otra' WHERE \"LeaseId\" = {0}", w.LeaseA));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("DELETE FROM \"DepositSettlements\" WHERE \"LeaseId\" = {0}", w.LeaseA));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"DepositDeductions\" SET \"Amount\" = 1 WHERE \"SettlementId\" IN (SELECT \"Id\" FROM \"DepositSettlements\" WHERE \"LeaseId\" = {0})", w.LeaseA));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("DELETE FROM \"DepositDeductions\" WHERE \"SettlementId\" IN (SELECT \"Id\" FROM \"DepositSettlements\" WHERE \"LeaseId\" = {0})", w.LeaseA));
        // The arithmetic is enforced by the database as well: refund must equal held minus deductions.
        await Sql("ALTER TABLE \"DepositSettlements\" DISABLE TRIGGER deposit_settlements_protect_trg");
        try { await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"DepositSettlements\" SET \"RefundAmount\" = 1 WHERE \"LeaseId\" = {0}", w.LeaseA)); }
        finally { await Sql("ALTER TABLE \"DepositSettlements\" ENABLE TRIGGER deposit_settlements_protect_trg"); }
    }

    [Fact]
    public async Task A_deposit_installment_coming_due_reminds_the_tenant_with_its_own_words()
    {
        var w = await NewWorld("depremind");
        await w.Owner.PostAsJsonAsync($"/api/v1/leases/{w.LeaseA}/deposit/schedule", Installments((D(Today.AddDays(3)), 8500m)));
        fx.Clock.Now = Local(Today, 9, 30);
        Assert.True((await RemindersFor(w.OrgId)).Ran);
        Assert.Equal(new[] { "Ana Ejemplo, tu abono del depósito vence en 3 días" }, await Titles(w.A));
    }

    // ---------- uploads (security review, Hito 16) ----------

    [Fact]
    public async Task One_person_cannot_upload_without_limit_in_a_day_and_the_limit_is_theirs_alone()
    {
        var w = await NewWorld("uploadcap");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        async Task<HttpStatusCode> Upload(HttpClient c, int n)
        {
            var part = new ByteArrayContent(png.Concat(BitConverter.GetBytes(n)).ToArray()); part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return (await c.PostAsync("/api/v1/files", new MultipartFormDataContent { { part, "file", "p.png" } })).StatusCode;
        }
        for (var i = 0; i < Habita.Domain.Files.StoredFile.MaxPerUserPerDay; i++) Assert.Equal(HttpStatusCode.Created, await Upload(w.A, i));
        Assert.Equal(HttpStatusCode.BadRequest, await Upload(w.A, 999_999));                // the 201st in 24 hours
        Assert.Equal(HttpStatusCode.Created, await Upload(w.B, 1));                          // another tenant is not affected
        Assert.Equal(HttpStatusCode.Created, await Upload(w.Owner, 2));
    }
}
