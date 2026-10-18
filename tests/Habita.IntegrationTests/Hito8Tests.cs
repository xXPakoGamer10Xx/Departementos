using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Habita.IntegrationTests;

public class Hito8Tests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    static string D(DateOnly d) => d.ToString("yyyy-MM-dd");
    static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City")).Date);
    static DateOnly M(int monthsAgo) => new DateOnly(Today.Year, Today.Month, 1).AddMonths(-monthsAgo);
    static DateOnly Last(DateOnly firstOfMonth) => firstOfMonth.AddMonths(1).AddDays(-1);

    int _unit;
    async Task<Guid> Property(HttpClient c, string name = "Torre Norte") =>
        (await Json(await c.PostAsJsonAsync("/api/v1/properties", new { name, address = "Reforma 100" }))).GetProperty("id").GetGuid();
    async Task<(Guid UnitId, Guid TenantId, Guid LeaseId)> Lease(HttpClient c, Guid propertyId, string tenant, decimal rent, int dueDay, string start = "2020-01-01")
    {
        var uid = (await Json(await c.PostAsJsonAsync($"/api/v1/properties/{propertyId}/units", new { code = $"R{Interlocked.Increment(ref _unit)}", baseRent = rent }))).GetProperty("id").GetGuid();
        var tid = (await Json(await c.PostAsJsonAsync("/api/v1/tenants", new { fullName = tenant }))).GetProperty("id").GetGuid();
        var lid = (await Json(await c.PostAsJsonAsync("/api/v1/leases", new { unitId = uid, tenantId = tid, startDate = start, monthlyRent = rent, dueDay }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/v1/leases/{lid}/activate", null)).StatusCode);
        return (uid, tid, lid);
    }
    static async Task<Guid> Manual(HttpClient c, Guid leaseId, decimal amount, DateOnly due, string description = "Cargo", int? year = null, int? month = null)
    {
        var r = await c.PostAsJsonAsync("/api/v1/rent-charges", new { leaseId, description, amount, dueDate = D(due), periodYear = year, periodMonth = month });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }
    static async Task<Guid> ChargeFor(HttpClient c, Guid leaseId, DateOnly month)
    {
        var items = (await Json(await c.GetAsync($"/api/v1/rent-charges?leaseId={leaseId}&year={month.Year}&month={month.Month}"))).GetProperty("items");
        return items[0].GetProperty("id").GetGuid();
    }
    static async Task<Guid> Pay(HttpClient c, Guid tenantId, decimal amount, DateOnly on, params (Guid Charge, decimal Amount)[] allocations)
    {
        var r = await c.PostAsJsonAsync("/api/v1/payments", new { tenantId, receivedOn = D(on), amount, method = "BankTransfer", allocations = allocations.Select(a => new { chargeId = a.Charge, amount = a.Amount }) });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }
    static async Task<JsonElement> Summary(HttpClient c, DateOnly from, DateOnly to) => await Json(await c.GetAsync($"/api/v1/reports/financial-summary?from={D(from)}&to={D(to)}"));
    static (int Count, decimal Total) Mc(JsonElement e) => (e.GetProperty("count").GetInt32(), e.GetProperty("total").GetDecimal());

    /// <summary>Known data: two tenants, rent for two past months, manual charges, three payments (one reversed).</summary>
    async Task<(HttpClient Owner, Guid OrgId)> Seeded(string tag)
    {
        var (owner, orgId, _) = await fx.RegisterAsync(tag);
        var pid = await Property(owner);
        var a = await Lease(owner, pid, "Ana Ejemplo", 8500m, 5);
        var b = await Lease(owner, pid, "Luis Prueba", 7000m, 15);
        foreach (var m in new[] { M(2), M(1) }) Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/v1/rent-charges/generate", new { year = m.Year, month = m.Month })).StatusCode);

        await Manual(owner, a.LeaseId, 500m, M(1).AddDays(19), "Cuota de agua");                                              // no period → belongs to its due month (M1)
        await Manual(owner, b.LeaseId, 300m, M(0).AddMonths(1).AddDays(4), "Reposición de llave", M(0).Year, M(0).Month);   // explicit period M0, due next month
        var voided = await Manual(owner, b.LeaseId, 100m, M(0).AddMonths(1).AddDays(4), "Cargo por error", M(0).Year, M(0).Month);
        await owner.PostAsJsonAsync($"/api/v1/rent-charges/{voided}/void", new { reason = "Capturado por error" });

        await Pay(owner, a.TenantId, 8500m, M(1).AddDays(9), (await ChargeFor(owner, a.LeaseId, M(2)), 8500m));                 // P1: pays A's M2 rent in full
        await Pay(owner, a.TenantId, 5000m, Today, (await ChargeFor(owner, a.LeaseId, M(1)), 3000m));                          // P2: today, 3,000 applied, 2,000 unassigned
        var p3 = await Pay(owner, b.TenantId, 7000m, M(2).AddDays(19), (await ChargeFor(owner, b.LeaseId, M(2)), 7000m));      // P3: then reversed
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v1/payments/{p3}/reverse", new { reason = "Transferencia devuelta" })).StatusCode);
        return (owner, orgId);
    }

    [Fact]
    public async Task Summary_separates_received_reversed_charged_voided_and_pending_with_exact_figures()
    {
        var (owner, _) = await Seeded("rep");

        // Month M1: P1 received; rent 15,500 (2); manual 500 (period = due month); nothing reversed or voided.
        var m1 = await Summary(owner, M(1), Last(M(1)));
        Assert.Equal((1, 8500m), Mc(m1.GetProperty("paymentsReceived")));
        Assert.Equal((0, 0m), Mc(m1.GetProperty("paymentsReversed")));
        Assert.Equal((2, 15500m), Mc(m1.GetProperty("rentChargesGenerated")));
        Assert.Equal((1, 500m), Mc(m1.GetProperty("otherChargesGenerated")));
        Assert.Equal((0, 0m), Mc(m1.GetProperty("chargesVoided")));

        // Month M2: the only payment was reversed, so it is NOT income; it shows separately under reversed.
        var m2 = await Summary(owner, M(2), Last(M(2)));
        Assert.Equal((0, 0m), Mc(m2.GetProperty("paymentsReceived")));
        Assert.Equal((1, 7000m), Mc(m2.GetProperty("paymentsReversed")));
        Assert.Equal((2, 15500m), Mc(m2.GetProperty("rentChargesGenerated")));

        // Current month: P2 received today; explicit-period manual charge counted; the voided one is shown apart, not in generated.
        var m0 = await Summary(owner, M(0), Last(M(0)));
        Assert.Equal((1, 5000m), Mc(m0.GetProperty("paymentsReceived")));
        Assert.Equal((0, 0m), Mc(m0.GetProperty("rentChargesGenerated")));
        Assert.Equal((1, 300m), Mc(m0.GetProperty("otherChargesGenerated")));
        Assert.Equal((1, 100m), Mc(m0.GetProperty("chargesVoided")));

        // State as of today (independent of the chosen period): charged 31,800 − applied 11,500 = 20,300 pending.
        Assert.Equal(20300m, m0.GetProperty("pendingBalance").GetDecimal());
        Assert.Equal(5, m0.GetProperty("openCharges").GetInt32());
        Assert.Equal(4, m0.GetProperty("overdueCharges").GetInt32());     // B-M2 7,000 + A-M1 5,500 + B-M1 7,000 + water 500
        Assert.Equal(20000m, m0.GetProperty("overdueBalance").GetDecimal());
        Assert.Equal(2, m0.GetProperty("tenantsWithOverdue").GetInt32());
        Assert.Equal(2000m, m0.GetProperty("unassignedCredit").GetDecimal()); // P2's 5,000 − 3,000; the reversed payment leaves no credit
        Assert.Equal(m1.GetProperty("pendingBalance").GetDecimal(), m0.GetProperty("pendingBalance").GetDecimal());
        Assert.Equal("MXN", m0.GetProperty("currency").GetString());
        Assert.Equal(D(Today), m0.GetProperty("asOf").GetString());

        // By-month breakdown over the three months.
        var range = await Summary(owner, M(2), Last(M(0)));
        var rows = range.GetProperty("byMonth").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        decimal Col(int i, string name) => rows[i].GetProperty(name).GetDecimal();
        Assert.Equal((15500m, 0m, 0m, 7000m), (Col(0, "rentCharged"), Col(0, "otherCharged"), Col(0, "received"), Col(0, "reversed")));
        Assert.Equal((15500m, 500m, 8500m, 0m), (Col(1, "rentCharged"), Col(1, "otherCharged"), Col(1, "received"), Col(1, "reversed")));
        Assert.Equal((0m, 300m, 5000m, 0m), (Col(2, "rentCharged"), Col(2, "otherCharged"), Col(2, "received"), Col(2, "reversed")));
        Assert.Equal(M(2).Month, rows[0].GetProperty("month").GetInt32());
    }

    [Fact]
    public async Task Dashboard_trend_covers_six_months_and_overdue_aging_buckets_add_up_to_the_overdue_balance()
    {
        var (owner, _) = await Seeded("chart8");
        var dash = await Json(await owner.GetAsync("/api/v1/dashboard"));
        var finance = dash.GetProperty("finance");

        // Six months ending at the selected (current) one, oldest first; months without activity are present with zeros.
        var trend = finance.GetProperty("byMonth").EnumerateArray().ToList();
        Assert.Equal(6, trend.Count);
        Assert.Equal((M(5).Year, M(5).Month), (trend[0].GetProperty("year").GetInt32(), trend[0].GetProperty("month").GetInt32()));
        Assert.Equal((M(0).Year, M(0).Month), (trend[5].GetProperty("year").GetInt32(), trend[5].GetProperty("month").GetInt32()));
        Assert.All(trend.Take(3), r => Assert.Equal(0m, r.GetProperty("rentCharged").GetDecimal() + r.GetProperty("received").GetDecimal()));
        Assert.Equal((15500m, 0m, 0m, 7000m), (trend[3].GetProperty("rentCharged").GetDecimal(), trend[3].GetProperty("otherCharged").GetDecimal(), trend[3].GetProperty("received").GetDecimal(), trend[3].GetProperty("reversed").GetDecimal()));
        Assert.Equal((15500m, 500m, 8500m), (trend[4].GetProperty("rentCharged").GetDecimal(), trend[4].GetProperty("otherCharged").GetDecimal(), trend[4].GetProperty("received").GetDecimal()));
        Assert.Equal((0m, 300m, 5000m), (trend[5].GetProperty("rentCharged").GetDecimal(), trend[5].GetProperty("otherCharged").GetDecimal(), trend[5].GetProperty("received").GetDecimal()));

        // Four overdue charges (7,000 / 5,500 / 7,000 / 500). Expected buckets come from their due dates and the organization's today.
        var overdue = new[] { (7000m, M(2).AddDays(14)), (5500m, M(1).AddDays(4)), (7000m, M(1).AddDays(14)), (500m, M(1).AddDays(19)) };
        (int Count, decimal Balance) Expected(int lo, int? hi) =>
            (overdue.Count(o => Today.DayNumber - o.Item2.DayNumber >= lo && (hi is null || Today.DayNumber - o.Item2.DayNumber <= hi)),
             overdue.Where(o => Today.DayNumber - o.Item2.DayNumber >= lo && (hi is null || Today.DayNumber - o.Item2.DayNumber <= hi)).Sum(o => o.Item1));
        var buckets = finance.GetProperty("overdueAging").EnumerateArray().ToList();
        Assert.Equal(["1–30 días", "31–60 días", "61–90 días", "Más de 90 días"], buckets.Select(b => b.GetProperty("label").GetString()!).ToArray());
        var ranges = new (int Lo, int? Hi)[] { (1, 30), (31, 60), (61, 90), (91, null) };
        for (var i = 0; i < 4; i++)
        {
            var (count, balance) = Expected(ranges[i].Lo, ranges[i].Hi);
            Assert.Equal(count, buckets[i].GetProperty("count").GetInt32());
            Assert.Equal(balance, buckets[i].GetProperty("balance").GetDecimal());
        }
        Assert.Equal(finance.GetProperty("overdueBalance").GetDecimal(), buckets.Sum(b => b.GetProperty("balance").GetDecimal())); // every overdue peso is in exactly one bucket
        Assert.Equal(finance.GetProperty("overdueCharges").GetInt32(), buckets.Sum(b => b.GetProperty("count").GetInt32()));
        Assert.Equal(4, buckets.Sum(b => b.GetProperty("count").GetInt32()));

        // Maintenance still receives no finance section at all, so no chart data either.
        var maint = await fx.AddMemberAsync(owner, "Maintenance");
        var raw = await (await maint.GetAsync("/api/v1/dashboard")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("overdueAging", raw);
        Assert.DoesNotContain("byMonth", raw);
    }

    [Fact]
    public async Task Reversing_a_payment_changes_pending_and_credit_immediately()
    {
        var (owner, _) = await Seeded("revrep");
        var before = await Summary(owner, M(0), Last(M(0)));
        var p2 = (await Json(await owner.GetAsync("/api/v1/payments?pageSize=50"))).GetProperty("items").EnumerateArray()
            .First(p => p.GetProperty("amount").GetDecimal() == 5000m && p.GetProperty("status").GetString() == "Confirmed").GetProperty("id").GetGuid();
        await owner.PostAsJsonAsync($"/api/v1/payments/{p2}/reverse", new { reason = "Duplicado" });
        var after = await Summary(owner, M(0), Last(M(0)));
        Assert.Equal(before.GetProperty("pendingBalance").GetDecimal() + 3000m, after.GetProperty("pendingBalance").GetDecimal()); // its 3,000 applied comes back
        Assert.Equal(0m, after.GetProperty("unassignedCredit").GetDecimal());                                                   // its 2,000 credit disappears
        Assert.Equal((0, 0m), Mc(after.GetProperty("paymentsReceived")));
        Assert.Equal((1, 5000m), Mc(after.GetProperty("paymentsReversed")));
    }

    [Fact]
    public async Task Income_uses_the_reception_date_with_inclusive_boundaries()
    {
        var (owner, _, _) = await fx.RegisterAsync("bound");
        var (_, tenant, lease) = await Lease(owner, await Property(owner), "Ana Ejemplo", 1000m, 5);
        var d = Today.AddDays(-10);
        await Pay(owner, tenant, 100m, d);
        await Pay(owner, tenant, 200m, d.AddDays(1));
        await Pay(owner, tenant, 400m, d.AddDays(2));
        Assert.Equal((2, 300m), Mc((await Summary(owner, d, d.AddDays(1))).GetProperty("paymentsReceived")));   // both endpoints included
        Assert.Equal((1, 400m), Mc((await Summary(owner, d.AddDays(2), d.AddDays(2))).GetProperty("paymentsReceived")));
        Assert.Equal((0, 0m), Mc((await Summary(owner, d.AddDays(3), d.AddDays(5))).GetProperty("paymentsReceived")));
        _ = lease;
    }

    [Fact]
    public async Task Summary_validates_the_range_and_org_dates_use_the_local_time_zone()
    {
        var (owner, orgId, _) = await fx.RegisterAsync("tz8");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/v1/reports/financial-summary?from={D(Today)}&to={D(Today.AddDays(-1))}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/v1/reports/financial-summary?from=1990-01-01&to=1990-02-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/v1/reports/financial-summary?from=2020-01-01&to=2030-01-01")).StatusCode); // too wide
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/v1/reports/financial-summary")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/v1/dashboard?year=2026&month=13")).StatusCode);

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""UPDATE "Organizations" SET "TimeZone" = 'Pacific/Kiritimati' WHERE "Id" = @o""", conn);
            cmd.Parameters.AddWithValue("o", orgId);
            await cmd.ExecuteNonQueryAsync();
        }
        var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Kiritimati")).Date);
        var dash = await Json(await owner.GetAsync("/api/v1/dashboard"));
        Assert.Equal(D(local), dash.GetProperty("asOf").GetString());
        Assert.Equal("Pacific/Kiritimati", dash.GetProperty("timeZone").GetString());
        Assert.Equal(local.Month, dash.GetProperty("month").GetInt32()); // default period is the organization's current local month
    }

    [Fact]
    public async Task Dashboard_occupancy_counts_active_units_with_maintenance_apart_and_archived_excluded()
    {
        var (owner, _, _) = await fx.RegisterAsync("occ8");
        var pid = await Property(owner);
        var (u1, _, _) = await Lease(owner, pid, "Ana Ejemplo", 1000m, 5, start: D(Today.AddDays(-30)));    // occupied
        var maint = (await Json(await owner.PostAsJsonAsync($"/api/v1/properties/{pid}/units", new { code = "MT", baseRent = 1 }))).GetProperty("id").GetGuid();
        await owner.PatchAsJsonAsync($"/api/v1/properties/{pid}/units/{maint}", new { status = "Maintenance" });
        await owner.PostAsJsonAsync($"/api/v1/properties/{pid}/units", new { code = "FREE", baseRent = 1 });
        var archived = (await Json(await owner.PostAsJsonAsync($"/api/v1/properties/{pid}/units", new { code = "OLD", baseRent = 1 }))).GetProperty("id").GetGuid();
        await owner.PatchAsJsonAsync($"/api/v1/properties/{pid}/units/{archived}", new { status = "Archived" });
        _ = u1;

        var occ = (await Json(await owner.GetAsync("/api/v1/dashboard"))).GetProperty("occupancy");
        Assert.Equal(3, occ.GetProperty("totalUnits").GetInt32());   // archived excluded
        Assert.Equal(1, occ.GetProperty("occupied").GetInt32());
        Assert.Equal(1, occ.GetProperty("maintenance").GetInt32());
        Assert.Equal(1, occ.GetProperty("available").GetInt32());
        Assert.Equal(0.3333m, occ.GetProperty("occupancyRate").GetDecimal());

        var empty = (await Json(await (await fx.RegisterAsync("occ8b")).Client.GetAsync("/api/v1/dashboard"))).GetProperty("occupancy");
        Assert.Equal(0, empty.GetProperty("totalUnits").GetInt32());
        Assert.Equal(0m, empty.GetProperty("occupancyRate").GetDecimal()); // no division by zero
    }

    [Fact]
    public async Task Dashboard_counts_open_issues_by_priority_and_ignores_resolved_and_closed()
    {
        var (owner, _, _) = await fx.RegisterAsync("iss8");
        var pid = await Property(owner);
        async Task<Guid> Issue(string priority) => (await Json(await owner.PostAsJsonAsync("/api/v1/maintenance-issues", new { propertyId = pid, title = $"i-{priority}", priority }))).GetProperty("id").GetGuid();
        await Issue("Urgent"); await Issue("Urgent"); await Issue("High"); await Issue("Normal");
        var done = await Issue("Low");
        await owner.PostAsJsonAsync($"/api/v1/maintenance-issues/{done}/status", new { status = "Resolved" });
        var inProgress = await Issue("High");
        await owner.PostAsJsonAsync($"/api/v1/maintenance-issues/{inProgress}/status", new { status = "InProgress" });

        var i = (await Json(await owner.GetAsync("/api/v1/dashboard"))).GetProperty("issues");
        Assert.Equal((2, 2, 1, 0, 5), (i.GetProperty("urgent").GetInt32(), i.GetProperty("high").GetInt32(), i.GetProperty("normal").GetInt32(), i.GetProperty("low").GetInt32(), i.GetProperty("total").GetInt32()));
    }

    [Fact]
    public async Task Maintenance_gets_a_dashboard_without_finance_and_no_report_access()
    {
        var (owner, _) = await Seeded("perm8");
        var maint = await fx.AddMemberAsync(owner, "Maintenance");
        var finance = await fx.AddMemberAsync(owner, "Finance");
        var readOnly = await fx.AddMemberAsync(owner, "ReadOnly");

        var raw = await (await maint.GetAsync("/api/v1/dashboard")).Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(raw))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("finance").ValueKind);
            Assert.True(doc.RootElement.GetProperty("occupancy").GetProperty("totalUnits").GetInt32() >= 0);
        }
        foreach (var leak in new[] { "pendingBalance", "paymentsReceived", "8500", "20300", "Ana Ejemplo", "Luis Prueba" }) Assert.DoesNotContain(leak, raw);

        var urls = new[] { $"/api/v1/reports/financial-summary?from={D(M(1))}&to={D(Last(M(1)))}", $"/api/v1/reports/charges.csv?from={D(M(1))}&to={D(Last(M(1)))}",
            $"/api/v1/reports/payments.csv?from={D(M(1))}&to={D(Last(M(1)))}", "/api/v1/reports/balances.csv" };
        foreach (var url in urls)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await maint.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await finance.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await fx.NewClient().GetAsync(url)).StatusCode);
        }
        var fin = await Json(await finance.GetAsync("/api/v1/dashboard"));
        Assert.Equal(20300m, fin.GetProperty("finance").GetProperty("pendingBalance").GetDecimal());
    }

    [Fact]
    public async Task Recent_activity_shows_only_safe_fields_and_only_what_each_role_may_see()
    {
        var (owner, _) = await Seeded("act8");
        var pid = (await Json(await owner.GetAsync("/api/v1/properties"))).GetProperty("items")[0].GetProperty("id").GetGuid();
        var maint = await fx.AddMemberAsync(owner, "Maintenance");
        await owner.PostAsJsonAsync("/api/v1/maintenance-issues", new { propertyId = pid, title = "Fuga de agua" });

        var ownerRaw = await (await owner.GetAsync("/api/v1/dashboard")).Content.ReadAsStringAsync();
        using var ownerDoc = JsonDocument.Parse(ownerRaw);
        var ownerActivity = ownerDoc.RootElement.GetProperty("activity").EnumerateArray().ToList();
        Assert.InRange(ownerActivity.Count, 1, 10);
        Assert.Equal("issue.created", ownerActivity[0].GetProperty("action").GetString()); // newest first
        Assert.Equal("Reportó una incidencia", ownerActivity[0].GetProperty("label").GetString());
        Assert.Equal("Usuario Prueba", ownerActivity[0].GetProperty("actorName").GetString());
        Assert.DoesNotContain("metadata", ownerRaw);
        Assert.DoesNotContain("@example.com", ownerRaw); // invitation emails live in audit metadata and must never surface

        var maintRaw = await (await maint.GetAsync("/api/v1/dashboard")).Content.ReadAsStringAsync();
        using var maintDoc = JsonDocument.Parse(maintRaw);
        var maintActions = maintDoc.RootElement.GetProperty("activity").EnumerateArray().Select(a => a.GetProperty("action").GetString()!).ToList();
        Assert.NotEmpty(maintActions);
        Assert.All(maintActions, a => Assert.StartsWith("issue.", a));

        var finance = await fx.AddMemberAsync(owner, "Finance");
        var financeActions = (await Json(await finance.GetAsync("/api/v1/dashboard"))).GetProperty("activity").EnumerateArray().Select(a => a.GetProperty("action").GetString()!).ToList();
        Assert.DoesNotContain(financeActions, a => a.StartsWith("membership.") || a.StartsWith("invitation.") || a.StartsWith("report."));
    }

    [Fact]
    public async Task OrganizationA_reports_and_exports_never_include_organizationB_data()
    {
        var (a, _) = await Seeded("isoA8");
        var (b, _, _) = await fx.RegisterAsync("isoB8");
        var emptySummary = await Summary(b, M(2), Last(M(0)));
        Assert.Equal((0, 0m), Mc(emptySummary.GetProperty("paymentsReceived")));
        Assert.Equal(0m, emptySummary.GetProperty("pendingBalance").GetDecimal());
        Assert.Equal(0m, emptySummary.GetProperty("unassignedCredit").GetDecimal());

        foreach (var url in new[] { $"/api/v1/reports/charges.csv?from={D(M(2))}&to={D(Last(M(0)))}", $"/api/v1/reports/payments.csv?from={D(M(2))}&to={D(Last(M(0)))}", "/api/v1/reports/balances.csv" })
        {
            var csv = await (await b.GetAsync(url)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("Ana Ejemplo", csv);
            Assert.DoesNotContain("Luis Prueba", csv);
            Assert.Single(csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)); // header only
            var own = await (await a.GetAsync(url)).Content.ReadAsStringAsync();
            Assert.Contains("Ana Ejemplo", own); // A's own export does contain it
        }
    }

    [Fact]
    public async Task Csv_exports_have_headers_bom_download_headers_filters_and_exact_rows()
    {
        var (owner, _) = await Seeded("csv8");
        var url = $"/api/v1/reports/charges.csv?from={D(M(1))}&to={D(Last(M(1)))}";
        var resp = await owner.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", resp.Content.Headers.ContentType!.ToString());
        Assert.Contains("attachment", resp.Content.Headers.ContentDisposition!.ToString());
        Assert.EndsWith(".csv\"", resp.Content.Headers.ContentDisposition.ToString());
        Assert.Contains("no-store", resp.Headers.CacheControl!.ToString());

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3).ToArray()); // BOM so Excel reads accents
        var lines = Encoding.UTF8.GetString(bytes).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Periodo,Inquilino,Propiedad,Unidad,Concepto,Tipo,Vence,Importe,Pagado,Saldo,Estado,Motivo de anulación", lines[0]);
        Assert.Equal(4, lines.Length); // header + 2 rent + 1 manual in M1
        var a = lines.Single(l => l.Contains("Ana Ejemplo") && l.Contains("Renta"));
        Assert.StartsWith($"{M(1):yyyy-MM},Ana Ejemplo,Torre Norte,", a);
        Assert.EndsWith(",8500.00,3000.00,5500.00,Vencido,", a);  // amount, paid, balance, effective status (past due)
        Assert.Contains(lines, l => l.Contains("Cuota de agua") && l.Contains(",Otro,"));

        var overdue = (await (await owner.GetAsync(url + "&status=Overdue")).Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, overdue.Length); // header + Luis rent + Ana rent + water: all three M1 charges are overdue
        var paid = (await (await owner.GetAsync($"/api/v1/reports/charges.csv?from={D(M(2))}&to={D(Last(M(2)))}&status=Paid")).Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, paid.Length); // header + Ana's fully paid M2 rent

        var voidedCsv = await (await owner.GetAsync($"/api/v1/reports/charges.csv?from={D(M(0))}&to={D(Last(M(0)))}&status=Voided")).Content.ReadAsStringAsync();
        Assert.Contains("Capturado por error", voidedCsv);
        Assert.Contains(",0.00,Anulado,", voidedCsv); // voided has zero balance

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/v1/reports/charges.csv?from={D(Today)}&to={D(Today.AddDays(-1))}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/v1/reports/charges.csv?from=2000-01-01&to=2100-01-01")).StatusCode);
    }

    [Fact]
    public async Task Payments_and_balances_exports_report_credit_reversals_and_days_late()
    {
        var (owner, _) = await Seeded("csv8b");
        var payments = (await (await owner.GetAsync($"/api/v1/reports/payments.csv?from={D(M(2))}&to={D(Today)}")).Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Fecha de recepción,Inquilino,Método,Referencia,Importe,Aplicado,Saldo a favor,Estado,Motivo del reverso", payments[0]);
        Assert.Equal(4, payments.Length);
        Assert.Contains(payments, l => l.Contains(",5000.00,3000.00,2000.00,Confirmado,"));                 // credit shown
        Assert.Contains(payments, l => l.Contains(",7000.00,0.00,0.00,Revertido,Transferencia devuelta"));  // reversed: no credit, reason kept
        Assert.Equal(2, (await (await owner.GetAsync($"/api/v1/reports/payments.csv?from={D(M(2))}&to={D(Today)}&status=Confirmed")).Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1);

        var balances = (await (await owner.GetAsync("/api/v1/reports/balances.csv")).Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Inquilino,Propiedad,Unidad,Concepto,Vence,Importe,Pagado,Saldo,Estado,Días de atraso", balances[0]);
        Assert.Equal(6, balances.Length); // header + 5 open charges (B-M2, A-M1, B-M1, water, B's future manual)
        Assert.DoesNotContain(balances, l => l.Contains("Capturado")); // voided and paid never appear
        var futureRow = balances.Single(l => l.Contains("Reposición de llave"));
        Assert.EndsWith(",300.00,0.00,300.00,Pendiente,0", futureRow); // not yet due: pending, zero days late
        var lateRows = balances.Where(l => l.Contains("Luis Prueba") && l.Contains("Renta")).ToList();
        Assert.Equal(2, lateRows.Count); // M2 (reopened by the reversal) and M1
        Assert.All(lateRows, l => Assert.Contains(",7000.00,0.00,7000.00,Vencido,", l));
    }

    [Fact]
    public async Task Csv_neutralizes_spreadsheet_formulas_and_quotes_awkward_text()
    {
        var (owner, _, _) = await fx.RegisterAsync("inj8");
        var pid = await Property(owner, "Torre \"Norte\", Fase 2");
        var tenant = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "=HYPERLINK(\"http://evil.example\",\"clic\")" }))).GetProperty("id").GetGuid();
        var uid = (await Json(await owner.PostAsJsonAsync($"/api/v1/properties/{pid}/units", new { code = "1", baseRent = 100 }))).GetProperty("id").GetGuid();
        var lid = (await Json(await owner.PostAsJsonAsync("/api/v1/leases", new { unitId = uid, tenantId = tenant, startDate = "2020-01-01", monthlyRent = 100, dueDay = 5 }))).GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/leases/{lid}/activate", null);
        await Manual(owner, lid, 50m, M(1).AddDays(4), "@SUM(1+1)\nsegunda línea, con coma", M(1).Year, M(1).Month);
        await Pay(owner, tenant, 20m, Today.AddDays(-1));

        var csv = await (await owner.GetAsync($"/api/v1/reports/charges.csv?from={D(M(1))}&to={D(Last(M(1)))}")).Content.ReadAsStringAsync();
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil.example\"\",\"\"clic\"\")\"", csv);           // formula neutralized, then RFC 4180 quoted
        Assert.DoesNotContain(",=HYPERLINK", csv);
        Assert.Contains("\"Torre \"\"Norte\"\", Fase 2\"", csv);                                      // quotes and comma escaped
        Assert.Contains("\"'@SUM(1+1)\nsegunda línea, con coma\"", csv);                              // leading @ neutralized; newline kept inside the quoted cell
        var pay = await (await owner.GetAsync($"/api/v1/reports/payments.csv?from={D(Today.AddDays(-1))}&to={D(Today)}")).Content.ReadAsStringAsync();
        Assert.Contains("\"'=HYPERLINK(", pay);
    }

    [Fact]
    public async Task Every_export_is_recorded_in_the_audit_log_with_the_filters_used()
    {
        var (owner, orgId, _) = await fx.RegisterAsync("aud8");
        await owner.GetAsync($"/api/v1/reports/charges.csv?from={D(M(1))}&to={D(Last(M(0)))}&status=Overdue");
        await owner.GetAsync($"/api/v1/reports/payments.csv?from={D(M(1))}&to={D(Last(M(0)))}");
        await owner.GetAsync("/api/v1/reports/balances.csv");
        await owner.GetAsync($"/api/v1/reports/charges.csv?from={D(Today)}&to={D(Today.AddDays(-5))}"); // rejected: must not be audited as an export

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT "Metadata"::text FROM "AuditLogs" WHERE "OrganizationId" = @o AND "Action" = 'report.exported' ORDER BY "OccurredAt" """, conn);
        cmd.Parameters.AddWithValue("o", orgId);
        var metas = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) metas.Add(rd.GetString(0));
        Assert.Equal(3, metas.Count);
        Assert.Contains("\"report\": \"charges\"", metas[0]); Assert.Contains("Overdue", metas[0]);
        Assert.Contains("\"report\": \"payments\"", metas[1]);
        Assert.Contains("\"report\": \"balances\"", metas[2]);
    }
}
