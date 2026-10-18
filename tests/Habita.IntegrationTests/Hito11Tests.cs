using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Habita.IntegrationTests;

/// <summary>Hito 11: the tenant portal. A tenant sees and does only what belongs to their own tenant record.</summary>
public class Hito11Tests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    static string D(DateOnly d) => d.ToString("yyyy-MM-dd");
    static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City")).Date);

    record World(HttpClient Owner, HttpClient A, HttpClient B, Guid TenantA, Guid TenantB, Guid LeaseA, Guid LeaseB);

    int _unit;
    async Task<Guid> Lease(HttpClient owner, Guid propertyId, Guid tenantId)
    {
        var uid = (await Json(await owner.PostAsJsonAsync($"/api/v1/properties/{propertyId}/units", new { code = $"P{Interlocked.Increment(ref _unit)}", baseRent = 1000 }))).GetProperty("id").GetGuid();
        var lid = (await Json(await owner.PostAsJsonAsync("/api/v1/leases", new { unitId = uid, tenantId, startDate = D(Today.AddMonths(-6)), monthlyRent = 1000, dueDay = 5 }))).GetProperty("id").GetGuid();
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

    async Task<World> NewWorld(string tag)
    {
        var (owner, _, _) = await fx.RegisterAsync(tag);
        var pid = (await Json(await owner.PostAsJsonAsync("/api/v1/properties", new { name = "Torre Norte", address = "Reforma 100" }))).GetProperty("id").GetGuid();
        var ta = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Ana Ejemplo", email = "ana@example.com" }))).GetProperty("id").GetGuid();
        var tb = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Luis Prueba", email = "luis@example.com" }))).GetProperty("id").GetGuid();
        var la = await Lease(owner, pid, ta); var lb = await Lease(owner, pid, tb);
        return new(owner, await Login(owner, ta), await Login(owner, tb), ta, tb, la, lb);
    }

    static async Task<Guid> Charge(HttpClient c, Guid leaseId, decimal amount, DateOnly due, string description)
    {
        var r = await c.PostAsJsonAsync("/api/v1/rent-charges", new { leaseId, description, amount, dueDate = D(due) });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    static async Task<Guid> Pay(HttpClient c, Guid tenantId, decimal amount, string? notes = null, params (Guid Charge, decimal Amount)[] allocs)
    {
        var r = await c.PostAsJsonAsync("/api/v1/payments", new
        {
            tenantId, receivedOn = D(Today), amount, method = "BankTransfer", reference = "REF-1", notes,
            allocations = allocs.Select(a => new { chargeId = a.Charge, amount = a.Amount }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    // ---------- account ----------

    [Fact]
    public async Task The_summary_shows_the_tenants_lease_what_they_owe_what_is_overdue_and_their_credit()
    {
        var w = await NewWorld("sum");
        var overdue = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-30), "Renta atrasada");
        var future = await Charge(w.Owner, w.LeaseA, 500m, Today.AddDays(20), "Renta próxima");
        var paid = await Charge(w.Owner, w.LeaseA, 300m, Today.AddDays(-10), "Cuota pagada");
        await Charge(w.Owner, w.LeaseB, 777m, Today.AddDays(-5), "Cargo de Luis");
        await Pay(w.Owner, w.TenantA, 300m, null, (paid, 300m));
        await Pay(w.Owner, w.TenantA, 1200m, null, (overdue, 200m)); // 1000 stays unassigned = credit

        var s = await Json(await w.A.GetAsync("/api/v1/portal/summary"));
        Assert.Equal("Ana Ejemplo", s.GetProperty("tenantName").GetString());
        Assert.Equal("MXN", s.GetProperty("currency").GetString());
        Assert.Equal(800m + 500m, s.GetProperty("totalDue").GetDecimal());
        Assert.Equal(800m, s.GetProperty("overdueAmount").GetDecimal());
        Assert.Equal(1, s.GetProperty("overdueCount").GetInt32());
        Assert.Equal(1000m, s.GetProperty("creditBalance").GetDecimal());
        Assert.Equal(future, s.GetProperty("nextCharge").GetProperty("id").GetGuid());
        var lease = s.GetProperty("lease");
        Assert.Equal("Torre Norte", lease.GetProperty("propertyName").GetString());
        Assert.Equal(1000m, lease.GetProperty("monthlyRent").GetDecimal());
        Assert.Equal(5, lease.GetProperty("dueDay").GetInt32());

        var b = await Json(await w.B.GetAsync("/api/v1/portal/summary")); // the other tenant sees only their own account
        Assert.Equal("Luis Prueba", b.GetProperty("tenantName").GetString());
        Assert.Equal(777m, b.GetProperty("totalDue").GetDecimal());
        Assert.Equal(0m, b.GetProperty("creditBalance").GetDecimal());
    }

    [Fact]
    public async Task A_tenant_without_a_lease_in_force_gets_an_empty_account_not_an_error()
    {
        var (owner, _, _) = await fx.RegisterAsync("nolease");
        var t = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Sin Contrato" }))).GetProperty("id").GetGuid();
        var me = await Login(owner, t);
        var s = await Json(await me.GetAsync("/api/v1/portal/summary"));
        Assert.Equal(JsonValueKind.Null, s.GetProperty("lease").ValueKind);
        Assert.Equal(0m, s.GetProperty("totalDue").GetDecimal());
        Assert.Equal(0, (await Json(await me.GetAsync("/api/v1/portal/charges"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Charges_hide_voided_ones_and_other_tenants_and_report_the_derived_status()
    {
        var w = await NewWorld("chg");
        var overdue = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-30), "Atrasada");
        await Charge(w.Owner, w.LeaseA, 500m, Today.AddDays(20), "Próxima");
        var paid = await Charge(w.Owner, w.LeaseA, 300m, Today.AddDays(-10), "Pagada");
        var voided = await Charge(w.Owner, w.LeaseA, 99m, Today.AddDays(1), "Anulada");
        await Charge(w.Owner, w.LeaseB, 777m, Today, "De Luis");
        await Pay(w.Owner, w.TenantA, 300m, null, (paid, 300m));
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/rent-charges/{voided}/void", new { reason = "Error" })).StatusCode);

        var all = await Json(await w.A.GetAsync("/api/v1/portal/charges"));
        var byName = all.GetProperty("items").EnumerateArray().ToDictionary(c => c.GetProperty("description").GetString()!);
        Assert.Equal(3, all.GetProperty("total").GetInt32());
        Assert.False(byName.ContainsKey("Anulada")); Assert.False(byName.ContainsKey("De Luis"));
        Assert.Equal("Overdue", byName["Atrasada"].GetProperty("status").GetString());
        Assert.Equal("Open", byName["Próxima"].GetProperty("status").GetString());
        Assert.Equal("Paid", byName["Pagada"].GetProperty("status").GetString());
        Assert.Equal(1000m, byName["Atrasada"].GetProperty("balance").GetDecimal());

        var pending = await Json(await w.A.GetAsync("/api/v1/portal/charges?pendingOnly=true"));
        Assert.Equal(2, pending.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await w.A.GetAsync($"/api/v1/portal/charges/{overdue}")).StatusCode); // there is no per-id route to probe
    }

    [Fact]
    public async Task Payments_show_the_tenants_own_and_never_staff_notes_or_reversal_reasons()
    {
        var w = await NewWorld("pay");
        var c1 = await Charge(w.Owner, w.LeaseA, 500m, Today.AddDays(5), "Renta");
        var good = await Pay(w.Owner, w.TenantA, 500m, "NOTA-INTERNA-SECRETA", (c1, 500m));
        var bad = await Pay(w.Owner, w.TenantA, 200m, "OTRA-NOTA");
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/payments/{bad}/reverse", new { reason = "MOTIVO-INTERNO-REVERSO" })).StatusCode);
        await Pay(w.Owner, w.TenantB, 123m, "PAGO-DE-LUIS");

        var resp = await w.A.GetAsync("/api/v1/portal/payments");
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("NOTA-INTERNA-SECRETA", raw); Assert.DoesNotContain("OTRA-NOTA", raw);
        Assert.DoesNotContain("MOTIVO-INTERNO-REVERSO", raw); Assert.DoesNotContain("PAGO-DE-LUIS", raw);
        Assert.DoesNotContain("createdBy", raw, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("tenantName", raw, StringComparison.OrdinalIgnoreCase);

        var items = JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var ok = items.Single(p => p.GetProperty("id").GetGuid() == good);
        Assert.Equal("Confirmed", ok.GetProperty("status").GetString());
        Assert.Equal("Renta", ok.GetProperty("allocations")[0].GetProperty("description").GetString());
        var reversed = items.Single(p => p.GetProperty("id").GetGuid() == bad);
        Assert.Equal("Reversed", reversed.GetProperty("status").GetString());
        Assert.Equal(0, reversed.GetProperty("allocations").GetArrayLength());
        Assert.Equal(0m, reversed.GetProperty("unassigned").GetDecimal());
    }

    // ---------- issues ----------

    async Task<Guid> Report(HttpClient tenant, string title = "Fuga en el baño", string? description = "Gotea el lavabo")
    {
        var r = await tenant.PostAsJsonAsync("/api/v1/portal/issues", new { title, description });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_reported_issue_lands_on_the_tenants_own_unit_as_normal_priority_and_staff_can_tell_it_came_from_a_tenant()
    {
        var w = await NewWorld("iss");
        var id = await Report(w.A);

        var mine = await Json(await w.A.GetAsync("/api/v1/portal/issues"));
        Assert.Equal(1, mine.GetProperty("total").GetInt32());
        Assert.Equal("Open", mine.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.StartsWith("P", mine.GetProperty("items")[0].GetProperty("unitCode").GetString());

        var staff = await Json(await w.Owner.GetAsync($"/api/v1/maintenance-issues/{id}"));
        Assert.True(staff.GetProperty("reportedByTenant").GetBoolean());
        Assert.Equal("Normal", staff.GetProperty("priority").GetString());
        Assert.Equal("Torre Norte", staff.GetProperty("propertyName").GetString());
        Assert.Equal(0, (await Json(await w.B.GetAsync("/api/v1/portal/issues"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_tenant_cannot_choose_where_the_issue_goes_or_how_urgent_it_is()
    {
        var w = await NewWorld("iss2");
        var r = await w.A.PostAsJsonAsync("/api/v1/portal/issues", new { title = "Foco", description = "x", priority = "Urgent", unitId = Guid.NewGuid(), propertyId = Guid.NewGuid(), tenantId = w.TenantB });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var staff = await Json(await w.Owner.GetAsync($"/api/v1/maintenance-issues/{(await Json(r)).GetProperty("id").GetGuid()}"));
        Assert.Equal("Normal", staff.GetProperty("priority").GetString());
        Assert.Equal(0, (await Json(await w.B.GetAsync("/api/v1/portal/issues"))).GetProperty("total").GetInt32()); // not Luis's
        foreach (var bad in new object[] { new { title = "", description = "x" }, new { title = new string('a', 201) }, new { title = "ok", description = new string('a', 4001) } })
            Assert.Equal(HttpStatusCode.BadRequest, (await w.A.PostAsJsonAsync("/api/v1/portal/issues", bad)).StatusCode);
    }

    [Fact]
    public async Task A_tenant_cannot_read_or_comment_on_another_tenants_issue()
    {
        var w = await NewWorld("iss3");
        var id = await Report(w.A);
        Assert.Equal(HttpStatusCode.NotFound, (await w.B.GetAsync($"/api/v1/portal/issues/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.B.PostAsJsonAsync($"/api/v1/portal/issues/{id}/comments", new { body = "hola" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.B.GetAsync($"/api/v1/portal/issues/{Guid.NewGuid()}")).StatusCode);
        // an issue staff created is not a tenant's either
        var pid = (await Json(await w.Owner.GetAsync($"/api/v1/maintenance-issues/{id}"))).GetProperty("propertyId").GetGuid();
        var staffIssue = (await Json(await w.Owner.PostAsJsonAsync("/api/v1/maintenance-issues", new { propertyId = pid, title = "Interna", description = "solo staff" }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await w.A.GetAsync($"/api/v1/portal/issues/{staffIssue}")).StatusCode);
    }

    [Fact]
    public async Task Staff_notes_stay_internal_unless_someone_marks_a_comment_visible_to_the_tenant()
    {
        var w = await NewWorld("vis");
        var id = await Report(w.A);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/maintenance-issues/{id}/comments", new { body = "INTERNO: el plomero cobra doble" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/maintenance-issues/{id}/comments", new { body = "Pasamos mañana a las 10", visibleToTenant = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/maintenance-issues/{id}/status", new { status = "InProgress", note = "NOTA-ESTADO-INTERNA" })).StatusCode);

        var raw = await (await w.A.GetAsync($"/api/v1/portal/issues/{id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("INTERNO", raw); Assert.DoesNotContain("NOTA-ESTADO-INTERNA", raw);
        Assert.Contains("Pasamos mañana a las 10", raw);
        var events = JsonDocument.Parse(raw).RootElement.GetProperty("events").EnumerateArray().ToList();
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "StatusChanged" && e.GetProperty("toStatus").GetString() == "InProgress");
        Assert.DoesNotContain("actor", raw, StringComparison.OrdinalIgnoreCase); // staff names are never exposed
        Assert.Equal("InProgress", JsonDocument.Parse(raw).RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("assigned", raw, StringComparison.OrdinalIgnoreCase);

        // staff see which comment the tenant can read
        var staff = await Json(await w.Owner.GetAsync($"/api/v1/maintenance-issues/{id}"));
        var comments = staff.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("type").GetString() == "Comment").ToList();
        Assert.Equal(new[] { false, true }, comments.Select(c => c.GetProperty("tenantVisible").GetBoolean()));
    }

    [Fact]
    public async Task A_tenant_comment_reaches_staff_and_a_closed_issue_refuses_more()
    {
        var w = await NewWorld("cmt");
        var id = await Report(w.A);
        var r = await w.A.PostAsJsonAsync($"/api/v1/portal/issues/{id}/comments", new { body = "  Sigue goteando  " });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var last = (await Json(r)).GetProperty("events").EnumerateArray().Last();
        Assert.True(last.GetProperty("fromTenant").GetBoolean());
        Assert.Equal("Sigue goteando", last.GetProperty("body").GetString());
        Assert.Contains("Sigue goteando", await (await w.Owner.GetAsync($"/api/v1/maintenance-issues/{id}")).Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await w.A.PostAsJsonAsync($"/api/v1/portal/issues/{id}/comments", new { body = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.A.PostAsJsonAsync($"/api/v1/portal/issues/{id}/comments", new { body = new string('a', 2001) })).StatusCode);
        foreach (var s in new[] { "Resolved", "Closed" })
            Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/maintenance-issues/{id}/status", new { status = s })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.A.PostAsJsonAsync($"/api/v1/portal/issues/{id}/comments", new { body = "ya" })).StatusCode);
    }

    [Fact]
    public async Task A_technician_sees_what_is_broken_but_not_which_tenant_reported_it()
    {
        var w = await NewWorld("tech");
        var id = await Report(w.A);
        await w.A.PostAsJsonAsync($"/api/v1/portal/issues/{id}/comments", new { body = "Estoy en casa por la tarde" });
        var tech = await fx.AddMemberAsync(w.Owner, "Maintenance");
        var uid = (await Json(await w.Owner.GetAsync("/api/v1/members"))).EnumerateArray().Single(m => m.GetProperty("role").GetString() == "Maintenance").GetProperty("userId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/maintenance-issues/{id}/assign", new { userId = uid })).StatusCode);

        var raw = await (await tech.GetAsync($"/api/v1/maintenance-issues/{id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Inquilino Portal", raw); // the login's real name
        Assert.Contains("\"createdByName\":\"Inquilino\"", raw);
        Assert.Contains("Estoy en casa por la tarde", raw);
    }

    [Fact]
    public async Task Reporting_is_refused_without_a_lease_in_force_and_past_ten_open_issues()
    {
        var (owner, _, _) = await fx.RegisterAsync("lim");
        var t = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Sin Contrato" }))).GetProperty("id").GetGuid();
        var noLease = await Login(owner, t);
        Assert.Equal(HttpStatusCode.BadRequest, (await noLease.PostAsJsonAsync("/api/v1/portal/issues", new { title = "Algo" })).StatusCode);

        var w = await NewWorld("lim2");
        for (var i = 0; i < 10; i++) await Report(w.A, $"Reporte {i}");
        Assert.Equal(HttpStatusCode.Conflict, (await w.A.PostAsJsonAsync("/api/v1/portal/issues", new { title = "Uno más" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await w.B.PostAsJsonAsync("/api/v1/portal/issues", new { title = "Luis sí puede" })).StatusCode); // the limit is per tenant
    }

    // ---------- access ----------

    [Fact]
    public async Task Only_a_tenant_login_can_use_the_portal()
    {
        var w = await NewWorld("who");
        var urls = new[] { "/api/v1/portal/summary", "/api/v1/portal/charges", "/api/v1/portal/payments", "/api/v1/portal/issues" };
        var staff = new List<HttpClient> { w.Owner };
        foreach (var role in new[] { "Admin", "Finance", "Maintenance", "ReadOnly" }) staff.Add(await fx.AddMemberAsync(w.Owner, role));
        var code = (await Json(await w.Owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Collector" }))).GetProperty("code").GetString();
        var collector = fx.NewClient();
        await collector.PostAsJsonAsync("/api/v1/auth/access-codes/redeem", new { code, email = $"c-{Guid.NewGuid():N}@example.com", fullName = "C", password = ApiFixture.Password });
        staff.Add(collector);

        foreach (var c in staff) foreach (var u in urls) Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync(u)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Owner.PostAsJsonAsync("/api/v1/portal/issues", new { title = "x" })).StatusCode);
        foreach (var u in urls) Assert.Equal(HttpStatusCode.Unauthorized, (await fx.NewClient().GetAsync(u)).StatusCode);
        foreach (var u in urls) Assert.Equal(HttpStatusCode.OK, (await w.A.GetAsync(u)).StatusCode);
    }

    [Fact]
    public async Task A_deactivated_tenant_login_can_be_replaced_by_a_new_account_and_the_old_one_stays_out()
    {
        var w = await NewWorld("swap");
        var member = (await Json(await w.Owner.GetAsync("/api/v1/members"))).EnumerateArray().First(m => m.GetProperty("tenantId").ValueKind == JsonValueKind.String && m.GetProperty("tenantId").GetGuid() == w.TenantA);
        // while the login is active no second one can be created
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Tenant", tenantId = w.TenantA })).StatusCode);
        await w.Owner.PatchAsJsonAsync($"/api/v1/members/{member.GetProperty("id").GetGuid()}", new { status = "Deactivated" });

        var fresh = await Login(w.Owner, w.TenantA); // new phone, new account
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/v1/portal/summary")).StatusCode);
        Assert.Equal("Ana Ejemplo", (await Json(await fresh.GetAsync("/api/v1/portal/summary"))).GetProperty("tenantName").GetString());
        Assert.NotEqual(HttpStatusCode.OK, (await w.A.GetAsync("/api/v1/portal/summary")).StatusCode); // the previous account lost it
        var members = (await Json(await w.Owner.GetAsync("/api/v1/members"))).EnumerateArray().Where(m => m.GetProperty("role").GetString() == "Tenant").ToList();
        Assert.Equal(2, members.Count); // Ana (new account) and Luis; still one login per tenant record
    }

    [Fact]
    public async Task A_tenant_login_that_staff_deactivate_loses_the_portal_immediately()
    {
        var w = await NewWorld("off");
        Assert.Equal(HttpStatusCode.OK, (await w.A.GetAsync("/api/v1/portal/summary")).StatusCode);
        var member = (await Json(await w.Owner.GetAsync("/api/v1/members"))).EnumerateArray().First(m => m.GetProperty("tenantId").ValueKind == JsonValueKind.String && m.GetProperty("tenantId").GetGuid() == w.TenantA);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PatchAsJsonAsync($"/api/v1/members/{member.GetProperty("id").GetGuid()}", new { status = "Deactivated" })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await w.A.GetAsync("/api/v1/portal/summary")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await w.A.GetAsync("/api/v1/portal/payments")).StatusCode);
    }
}
