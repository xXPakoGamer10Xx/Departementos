using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Habita.Api.Operations;
using Xunit;

namespace Habita.IntegrationTests;

public class Hito9SeedTests(SeededApiFixture fx) : IClassFixture<SeededApiFixture>
{
    string _report => fx.Report;

    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    async Task<HttpClient> As(string email)
    {
        var c = fx.NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/v1/auth/login", new { email, password = DemoSeeder.Password })).StatusCode);
        return c;
    }

    [Fact]
    public async Task Seeds_the_spec_scenario_with_fictitious_data_and_is_idempotent()
    {
        Assert.Contains("Datos de demostración creados", _report);
        Assert.Contains("Habita-Demo-2026!", _report);
        Assert.Contains("ya existe", await DemoSeeder.RunAsync(fx.Factory.Services)); // second run does nothing

        var owner = await As("propietario@demo.example");
        var me = await Json(await owner.GetAsync("/api/v1/me"));
        Assert.Equal("Residencial Demo", me.GetProperty("organizations")[0].GetProperty("name").GetString());
        Assert.Equal("MXN", me.GetProperty("organizations")[0].GetProperty("currency").GetString());

        var props = (await Json(await owner.GetAsync("/api/v1/properties"))).GetProperty("items");
        Assert.Equal(1, props.GetArrayLength());
        Assert.Equal("Torre Norte", props[0].GetProperty("name").GetString());
        var units = (await Json(await owner.GetAsync($"/api/v1/properties/{props[0].GetProperty("id").GetGuid()}/units"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["101", "102", "201"], units.Select(u => u.GetProperty("code").GetString()!).ToArray());
        Assert.All(units, u => Assert.Equal(8500m, u.GetProperty("baseRent").GetDecimal()));
        Assert.Equal("Occupied", units[0].GetProperty("status").GetString());   // Ana
        Assert.Equal("Occupied", units[1].GetProperty("status").GetString());   // Luis
        Assert.Equal("Available", units[2].GetProperty("status").GetString());  // previous lease ended

        var tenants = (await Json(await owner.GetAsync("/api/v1/tenants"))).GetProperty("items").EnumerateArray().Select(t => t.GetProperty("fullName").GetString()!).ToList();
        Assert.Equal(["Ana Ejemplo", "Luis Prueba"], tenants);
        Assert.All((await Json(await owner.GetAsync("/api/v1/tenants"))).GetProperty("items").EnumerateArray(), t => Assert.EndsWith("@example.com", t.GetProperty("email").GetString()));
    }

    [Fact]
    public async Task Contains_the_edge_cases_the_spec_asks_for()
    {
        var owner = await As("propietario@demo.example");
        var leases = (await Json(await owner.GetAsync("/api/v1/leases?pageSize=50"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(1, leases.Count(l => l.GetProperty("status").GetString() == "Ended"));   // terminated lease
        Assert.Equal(2, leases.Count(l => l.GetProperty("status").GetString() == "Active"));

        var charges = (await Json(await owner.GetAsync("/api/v1/rent-charges?pageSize=100"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(charges, c => c.GetProperty("status").GetString() == "Overdue");       // overdue charge
        Assert.Contains(charges, c => c.GetProperty("paidAmount").GetDecimal() == 5000m && c.GetProperty("balance").GetDecimal() == 3500m); // partially paid (the $5,000 example)
        Assert.Contains(charges, c => c.GetProperty("status").GetString() == "Paid");

        var payments = (await Json(await owner.GetAsync("/api/v1/payments?pageSize=100"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(payments, p => p.GetProperty("status").GetString() == "Reversed" && p.GetProperty("reversalReason").GetString() == "Transferencia devuelta por el banco");
        Assert.Contains(payments, p => p.GetProperty("amount").GetDecimal() == 5000m && p.GetProperty("allocatedAmount").GetDecimal() == 5000m);
        var credit = payments.Single(p => p.GetProperty("unassignedAmount").GetDecimal() > 0);
        Assert.Equal(2000m, credit.GetProperty("unassignedAmount").GetDecimal());
        Assert.Contains(credit.GetProperty("id").GetGuid().ToString(), _report);                 // the report names it for manual concurrency tests

        var issues = (await Json(await owner.GetAsync("/api/v1/maintenance-issues"))).GetProperty("items").EnumerateArray().ToList();
        var leak = issues.Single(i => i.GetProperty("title").GetString() == "Fuga en lavabo");
        Assert.Equal("Normal", leak.GetProperty("priority").GetString());
        Assert.Equal("InProgress", leak.GetProperty("status").GetString());
        Assert.Equal("Mario Mantenimiento", leak.GetProperty("assignedToName").GetString());

        var summary = await Json(await owner.GetAsync("/api/v1/dashboard"));
        Assert.Equal(2000m, summary.GetProperty("finance").GetProperty("unassignedCredit").GetDecimal());
        Assert.True(summary.GetProperty("finance").GetProperty("overdueCharges").GetInt32() >= 4);
        Assert.True(summary.GetProperty("finance").GetProperty("tenantsWithOverdue").GetInt32() == 2);
    }

    [Fact]
    public async Task Users_without_permission_exist_for_each_role_and_hit_the_expected_walls()
    {
        var maint = await As("mantenimiento@demo.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await maint.GetAsync("/api/v1/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maint.GetAsync("/api/v1/leases")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await maint.GetAsync("/api/v1/maintenance-issues")).StatusCode);

        var readOnly = await As("lectura@demo.example");
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync("/api/v1/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync("/api/v1/properties", new { name = "x", address = "y" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await (await As("finanzas@demo.example")).GetAsync("/api/v1/rent-charges")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await As("admin@demo.example")).GetAsync("/api/v1/audit-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await As("finanzas@demo.example")).GetAsync("/api/v1/audit-logs")).StatusCode);
    }

    [Fact]
    public async Task The_other_organization_exists_to_try_foreign_uuids_and_stays_out_of_reach()
    {
        var foreignPayment = System.Text.RegularExpressions.Regex.Match(_report, @"Pago ajeno: ([0-9a-f-]{36})").Groups[1].Value;
        var foreignCharge = System.Text.RegularExpressions.Regex.Match(_report, @"cargo ajeno: ([0-9a-f-]{36})").Groups[1].Value;
        var owner = await As("propietario@demo.example");

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/payments/{foreignPayment}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/rent-charges/{foreignCharge}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/v1/payments/{foreignPayment}/reverse", new { reason = "intento" })).StatusCode);
        Assert.DoesNotContain("Privada Ajena", await (await owner.GetAsync("/api/v1/properties")).Content.ReadAsStringAsync());

        var other = await As("otra@demo.example");
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"/api/v1/payments/{foreignPayment}")).StatusCode);
        Assert.Contains("Privada Ajena", await (await other.GetAsync("/api/v1/properties")).Content.ReadAsStringAsync());
        Assert.DoesNotContain("Torre Norte", await (await other.GetAsync("/api/v1/properties")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Seeded_actions_are_in_the_audit_log_with_real_actors()
    {
        var admin = await As("admin@demo.example");
        var items = (await Json(await admin.GetAsync("/api/v1/audit-logs?pageSize=100"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, i => i.GetProperty("action").GetString() == "payment.reversed" && i.GetProperty("actorName").GetString() == "Paola Propietaria");
        Assert.Contains(items, i => i.GetProperty("action").GetString() == "issue.resolved" || i.GetProperty("action").GetString() == "issue.status_changed" && i.GetProperty("actorName").GetString() == "Mario Mantenimiento");
        Assert.Contains(items, i => i.GetProperty("action").GetString() == "charges.generated");
    }
}

/// <summary>Mutates the demo credit, so it gets its own freshly seeded database.</summary>
public class Hito9SeedConcurrencyTests(SeededApiFixture fx) : IClassFixture<SeededApiFixture>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Two_concurrent_requests_cannot_spend_the_demo_credit_twice()
    {
        var owner = fx.NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync("/api/v1/auth/login", new { email = "finanzas@demo.example", password = DemoSeeder.Password })).StatusCode);
        var credit = (await Json(await owner.GetAsync("/api/v1/payments?pageSize=100"))).GetProperty("items").EnumerateArray().Single(p => p.GetProperty("unassignedAmount").GetDecimal() > 0);
        var paymentId = credit.GetProperty("id").GetGuid();
        var open = (await Json(await owner.GetAsync($"/api/v1/rent-charges?tenantId={credit.GetProperty("tenantId").GetGuid()}&openOnly=true"))).GetProperty("items").EnumerateArray().Take(2).ToList();

        var results = await Task.WhenAll(open.Select(c => owner.PostAsJsonAsync($"/api/v1/payments/{paymentId}/allocations",
            new { allocations = new[] { new { chargeId = c.GetProperty("id").GetGuid(), amount = 1500m } } })));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK)); // 1,500 fits once in the 2,000 credit, never twice
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(500m, (await Json(await owner.GetAsync($"/api/v1/payments/{paymentId}"))).GetProperty("unassignedAmount").GetDecimal());
    }

}
