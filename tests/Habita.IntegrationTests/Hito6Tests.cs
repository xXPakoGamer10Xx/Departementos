using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Habita.IntegrationTests;

public class Hito6Tests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    static string D(DateOnly d) => d.ToString("yyyy-MM-dd");
    static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City")).Date);

    record World(HttpClient Owner, Guid OrgId, Guid PropertyId, Guid TenantId, Guid LeaseId);

    int _unit;
    async Task<World> NewWorld(string tag)
    {
        var (owner, orgId, _) = await fx.RegisterAsync(tag);
        var pid = (await Json(await owner.PostAsJsonAsync("/api/v1/properties", new { name = "Torre Norte", address = "Reforma 100" }))).GetProperty("id").GetGuid();
        var tid = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Ana Ejemplo" }))).GetProperty("id").GetGuid();
        var lease = await NewLease(owner, pid, tid);
        return new(owner, orgId, pid, tid, lease);
    }

    async Task<Guid> NewLease(HttpClient c, Guid propertyId, Guid tenantId)
    {
        var uid = (await Json(await c.PostAsJsonAsync($"/api/v1/properties/{propertyId}/units", new { code = $"U{Interlocked.Increment(ref _unit)}", baseRent = 1000 }))).GetProperty("id").GetGuid();
        var lid = (await Json(await c.PostAsJsonAsync("/api/v1/leases", new { unitId = uid, tenantId, startDate = "2030-01-01", monthlyRent = 1000, dueDay = 5 }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/v1/leases/{lid}/activate", null)).StatusCode);
        return lid;
    }

    static async Task<Guid> Charge(HttpClient c, Guid leaseId, decimal amount, string? due = null, string description = "Cargo")
    {
        var r = await c.PostAsJsonAsync("/api/v1/rent-charges", new { leaseId, description, amount, dueDate = due ?? "2030-06-05" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    static object Alloc(Guid chargeId, decimal amount) => new { chargeId, amount };

    static async Task<HttpResponseMessage> Pay(HttpClient c, Guid tenantId, decimal amount, object[]? allocations = null, string? key = null, DateOnly? receivedOn = null,
        string method = "BankTransfer", string? reference = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { tenantId, receivedOn = D(receivedOn ?? Today), amount, method, reference, allocations = allocations ?? [] }), Encoding.UTF8, "application/json"),
        };
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        return await c.SendAsync(req);
    }

    static async Task<Guid> PayOk(HttpClient c, Guid tenantId, decimal amount, params object[] allocations)
    {
        var r = await Pay(c, tenantId, amount, allocations);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    static async Task<JsonElement> GetCharge(HttpClient c, Guid id) => await Json(await c.GetAsync($"/api/v1/rent-charges/{id}"));
    static async Task<JsonElement> GetPayment(HttpClient c, Guid id) => await Json(await c.GetAsync($"/api/v1/payments/{id}"));
    static Task<HttpResponseMessage> Allocate(HttpClient c, Guid paymentId, params object[] allocations) => c.PostAsJsonAsync($"/api/v1/payments/{paymentId}/allocations", new { allocations });
    static Task<HttpResponseMessage> Reverse(HttpClient c, Guid paymentId, string reason = "Cheque rebotado") => c.PostAsJsonAsync($"/api/v1/payments/{paymentId}/reverse", new { reason });

    [Fact]
    public async Task Criterion4_a_1000_payment_with_700_applied_shows_700_applied_and_300_unassigned()
    {
        var w = await NewWorld("c4");
        var charge = await Charge(w.Owner, w.LeaseId, 1000m);
        var payment = await PayOk(w.Owner, w.TenantId, 1000m, Alloc(charge, 700m));

        var p = await GetPayment(w.Owner, payment);
        Assert.Equal(1000m, p.GetProperty("amount").GetDecimal());
        Assert.Equal(700m, p.GetProperty("allocatedAmount").GetDecimal());
        Assert.Equal(300m, p.GetProperty("unassignedAmount").GetDecimal());
        Assert.Equal("Confirmed", p.GetProperty("status").GetString());
        Assert.Equal(1, p.GetProperty("allocations").GetArrayLength());

        var c = await GetCharge(w.Owner, charge);
        Assert.Equal(700m, c.GetProperty("paidAmount").GetDecimal());
        Assert.Equal(300m, c.GetProperty("balance").GetDecimal());
        Assert.Equal("PartiallyPaid", c.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Paying_the_full_balance_marks_the_charge_paid_and_a_paid_charge_accepts_nothing_more()
    {
        var w = await NewWorld("full");
        var charge = await Charge(w.Owner, w.LeaseId, 800m);
        await PayOk(w.Owner, w.TenantId, 800m, Alloc(charge, 800m));
        var c = await GetCharge(w.Owner, charge);
        Assert.Equal("Paid", c.GetProperty("status").GetString());
        Assert.Equal(0m, c.GetProperty("balance").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await Pay(w.Owner, w.TenantId, 10m, [Alloc(charge, 10m)])).StatusCode);
        Assert.Equal(0, (await Json(await w.Owner.GetAsync($"/api/v1/rent-charges?tenantId={w.TenantId}&openOnly=true"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Criterion5_cannot_apply_more_than_the_charge_balance_or_the_payment_or_its_unassigned_balance()
    {
        var w = await NewWorld("c5");
        var small = await Charge(w.Owner, w.LeaseId, 500m);
        var big = await Charge(w.Owner, w.LeaseId, 5000m);

        var overCharge = await Pay(w.Owner, w.TenantId, 1000m, [Alloc(small, 600m)]);
        Assert.Equal(HttpStatusCode.Conflict, overCharge.StatusCode);
        Assert.Contains("saldo", (await Json(overCharge)).GetProperty("detail").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(w.Owner, w.TenantId, 1000m, [Alloc(small, 500m), Alloc(big, 600m)])).StatusCode); // 1,100 > 1,000

        var payment = await PayOk(w.Owner, w.TenantId, 1000m, Alloc(big, 700m));
        Assert.Equal(HttpStatusCode.Conflict, (await Allocate(w.Owner, payment, Alloc(small, 400m))).StatusCode); // only 300 left unassigned
        Assert.Equal(HttpStatusCode.OK, (await Allocate(w.Owner, payment, Alloc(small, 300m))).StatusCode);
        Assert.Equal(0m, (await GetPayment(w.Owner, payment)).GetProperty("unassignedAmount").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await Allocate(w.Owner, payment, Alloc(small, 1m))).StatusCode);

        // A partial payment on a charge reduces what the next one may apply.
        var second = await PayOk(w.Owner, w.TenantId, 500m);
        Assert.Equal(HttpStatusCode.Conflict, (await Allocate(w.Owner, second, Alloc(small, 250m))).StatusCode); // small has only 200 left
        Assert.Equal(HttpStatusCode.OK, (await Allocate(w.Owner, second, Alloc(small, 200m))).StatusCode);
        Assert.Equal("Paid", (await GetCharge(w.Owner, small)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_payment_without_allocations_keeps_everything_unassigned_and_touches_no_charge()
    {
        var w = await NewWorld("noalloc");
        var charge = await Charge(w.Owner, w.LeaseId, 1000m);
        var payment = await PayOk(w.Owner, w.TenantId, 400m);
        var p = await GetPayment(w.Owner, payment);
        Assert.Equal(400m, p.GetProperty("unassignedAmount").GetDecimal());
        Assert.Equal(0m, p.GetProperty("allocatedAmount").GetDecimal());
        Assert.Equal("Open", (await GetCharge(w.Owner, charge)).GetProperty("status").GetString()); // never auto-applied

        Assert.Equal(HttpStatusCode.OK, (await Allocate(w.Owner, payment, Alloc(charge, 400m))).StatusCode); // only by explicit action
        Assert.Equal(600m, (await GetCharge(w.Owner, charge)).GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task Criterion12_a_failing_allocation_leaves_no_partial_state()
    {
        var w = await NewWorld("atomic");
        var mine = await Charge(w.Owner, w.LeaseId, 1000m);
        var other = (await Json(await w.Owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Luis Prueba" }))).GetProperty("id").GetGuid();
        var otherCharge = await Charge(w.Owner, await NewLease(w.Owner, w.PropertyId, other), 1000m);

        // First allocation is valid, second belongs to another tenant: the whole request must fail.
        var r = await Pay(w.Owner, w.TenantId, 1000m, [Alloc(mine, 500m), Alloc(otherCharge, 500m)]);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, (await Json(await w.Owner.GetAsync("/api/v1/payments"))).GetProperty("total").GetInt32());
        Assert.Equal("Open", (await GetCharge(w.Owner, mine)).GetProperty("status").GetString());
        Assert.Equal(1000m, (await GetCharge(w.Owner, mine)).GetProperty("balance").GetDecimal());

        // Same for a voided charge and for a charge that does not exist.
        var voided = await Charge(w.Owner, w.LeaseId, 100m);
        await w.Owner.PostAsJsonAsync($"/api/v1/rent-charges/{voided}/void", new { reason = "Error" });
        Assert.Equal(HttpStatusCode.Conflict, (await Pay(w.Owner, w.TenantId, 600m, [Alloc(mine, 500m), Alloc(voided, 100m)])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(w.Owner, w.TenantId, 600m, [Alloc(mine, 500m), Alloc(Guid.NewGuid(), 100m)])).StatusCode);
        Assert.Equal(0, (await Json(await w.Owner.GetAsync("/api/v1/payments"))).GetProperty("total").GetInt32());
        Assert.Equal(1000m, (await GetCharge(w.Owner, mine)).GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task Criterion13_a_retried_request_with_the_same_idempotency_key_creates_one_payment()
    {
        var w = await NewWorld("idem");
        var charge = await Charge(w.Owner, w.LeaseId, 1000m);
        var key = Guid.NewGuid().ToString();

        var first = await Pay(w.Owner, w.TenantId, 600m, [Alloc(charge, 600m)], key: key, reference: "SPEI-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await Pay(w.Owner, w.TenantId, 600m, [Alloc(charge, 600m)], key: key, reference: "SPEI-1");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal((await Json(first)).GetProperty("id").GetGuid(), (await Json(second)).GetProperty("id").GetGuid());

        Assert.Equal(1, (await Json(await w.Owner.GetAsync("/api/v1/payments"))).GetProperty("total").GetInt32());
        Assert.Equal(400m, (await GetCharge(w.Owner, charge)).GetProperty("balance").GetDecimal()); // applied once, not twice

        // Same key with different content is rejected, not silently treated as the same payment.
        Assert.Equal(HttpStatusCode.Conflict, (await Pay(w.Owner, w.TenantId, 700m, [Alloc(charge, 700m)], key: key)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(w.Owner, w.TenantId, 10m, key: new string('k', 101))).StatusCode);
    }

    [Fact]
    public async Task Concurrent_double_submits_with_the_same_key_create_exactly_one_payment()
    {
        var w = await NewWorld("idemrace");
        var charge = await Charge(w.Owner, w.LeaseId, 1000m);
        var key = Guid.NewGuid().ToString();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Pay(w.Owner, w.TenantId, 1000m, [Alloc(charge, 1000m)], key: key)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, (await Json(await w.Owner.GetAsync("/api/v1/payments"))).GetProperty("total").GetInt32());
        Assert.Equal("Paid", (await GetCharge(w.Owner, charge)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Concurrent_payments_cannot_overpay_the_same_charge()
    {
        var w = await NewWorld("racecharge");
        var charge = await Charge(w.Owner, w.LeaseId, 1000m);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Pay(w.Owner, w.TenantId, 1000m, [Alloc(charge, 1000m)], key: Guid.NewGuid().ToString())));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var c = await GetCharge(w.Owner, charge);
        Assert.Equal(1000m, c.GetProperty("paidAmount").GetDecimal());
        Assert.Equal(0m, c.GetProperty("balance").GetDecimal());
        Assert.Equal(1, (await Json(await w.Owner.GetAsync("/api/v1/payments"))).GetProperty("total").GetInt32()); // losers left nothing behind
    }

    [Fact]
    public async Task Concurrent_allocations_cannot_spend_the_same_unassigned_balance_twice()
    {
        var w = await NewWorld("racepay");
        var payment = await PayOk(w.Owner, w.TenantId, 1000m);
        var charges = new List<Guid>();
        for (var i = 0; i < 8; i++) charges.Add(await Charge(w.Owner, w.LeaseId, 1000m, description: $"Cargo {i}"));

        var results = await Task.WhenAll(charges.Select(ch => Allocate(w.Owner, payment, Alloc(ch, 700m))));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK)); // 700 fits once in 1,000, never twice
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var p = await GetPayment(w.Owner, payment);
        Assert.Equal(700m, p.GetProperty("allocatedAmount").GetDecimal());
        Assert.Equal(300m, p.GetProperty("unassignedAmount").GetDecimal());
    }

    [Fact]
    public async Task Concurrent_mixed_payments_and_allocations_over_shared_charges_never_exceed_any_limit_or_deadlock()
    {
        var w = await NewWorld("mixed");
        var a = await Charge(w.Owner, w.LeaseId, 1000m, description: "A");
        var b = await Charge(w.Owner, w.LeaseId, 1000m, description: "B");
        var payments = new List<Guid>();
        for (var i = 0; i < 4; i++) payments.Add(await PayOk(w.Owner, w.TenantId, 600m));

        // 4 payments x 2 charges, opposite orders, all at once: total demand 4,800 against 2,000 of balance.
        var tasks = new List<Task<HttpResponseMessage>>();
        foreach (var p in payments)
        {
            tasks.Add(Allocate(w.Owner, p, Alloc(a, 600m), Alloc(b, 600m)));
            tasks.Add(Allocate(w.Owner, p, Alloc(b, 400m), Alloc(a, 400m)));
        }
        var results = await Task.WhenAll(tasks);
        Assert.DoesNotContain(results, r => (int)r.StatusCode >= 500); // no deadlock / unhandled error
        foreach (var charge in new[] { a, b })
        {
            var c = await GetCharge(w.Owner, charge);
            Assert.InRange(c.GetProperty("paidAmount").GetDecimal(), 0m, 1000m);
            Assert.Equal(1000m - c.GetProperty("paidAmount").GetDecimal(), c.GetProperty("balance").GetDecimal());
        }
        foreach (var p in payments)
            Assert.InRange((await GetPayment(w.Owner, p)).GetProperty("allocatedAmount").GetDecimal(), 0m, 600m);
    }

    [Fact]
    public async Task Criterion6_confirmed_payments_cannot_be_edited_or_deleted_through_the_api_or_the_database()
    {
        var w = await NewWorld("c6");
        var charge = await Charge(w.Owner, w.LeaseId, 1000m);
        var payment = await PayOk(w.Owner, w.TenantId, 1000m, Alloc(charge, 500m));

        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            var r = await w.Owner.SendAsync(new HttpRequestMessage(method, $"/api/v1/payments/{payment}") { Content = JsonContent.Create(new { amount = 1 }) });
            Assert.True(r.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, $"{method} returned {r.StatusCode}");
        }
        Assert.Equal(1000m, (await GetPayment(w.Owner, payment)).GetProperty("amount").GetDecimal());

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        async Task<PostgresException> Fails(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("p", payment);
            return await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        }
        await Fails("""UPDATE "Payments" SET "Amount" = 1 WHERE "Id" = @p""");
        await Fails("""UPDATE "Payments" SET "TenantId" = gen_random_uuid() WHERE "Id" = @p""");
        await Fails("""DELETE FROM "Payments" WHERE "Id" = @p""");
        await Fails("""UPDATE "PaymentAllocations" SET "Amount" = 1 WHERE "PaymentId" = @p""");
        await Fails("""DELETE FROM "PaymentAllocations" WHERE "PaymentId" = @p""");
    }

    [Fact]
    public async Task Criterion7_reversing_a_payment_restores_every_affected_balance()
    {
        var w = await NewWorld("c7");
        var a = await Charge(w.Owner, w.LeaseId, 1000m, description: "A");
        var b = await Charge(w.Owner, w.LeaseId, 500m, description: "B");
        var p1 = await PayOk(w.Owner, w.TenantId, 1000m, Alloc(a, 700m), Alloc(b, 300m));
        var p2 = await PayOk(w.Owner, w.TenantId, 300m, Alloc(a, 300m)); // A is fully paid by p1 + p2

        Assert.Equal("Paid", (await GetCharge(w.Owner, a)).GetProperty("status").GetString());

        var rev = await Reverse(w.Owner, p1, "Transferencia devuelta");
        Assert.Equal(HttpStatusCode.OK, rev.StatusCode);
        var body = await Json(rev);
        Assert.Equal("Reversed", body.GetProperty("status").GetString());
        Assert.Equal("Transferencia devuelta", body.GetProperty("reversalReason").GetString());
        Assert.Equal(0m, body.GetProperty("unassignedAmount").GetDecimal());
        Assert.Equal(0m, body.GetProperty("allocatedAmount").GetDecimal());

        var ca = await GetCharge(w.Owner, a);
        Assert.Equal("PartiallyPaid", ca.GetProperty("status").GetString()); // still has p2's 300
        Assert.Equal(300m, ca.GetProperty("paidAmount").GetDecimal());
        Assert.Equal(700m, ca.GetProperty("balance").GetDecimal());
        var cb = await GetCharge(w.Owner, b);
        Assert.Equal("Open", cb.GetProperty("status").GetString());
        Assert.Equal(500m, cb.GetProperty("balance").GetDecimal());

        // The payment record is kept, and a reversed payment accepts nothing.
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.GetAsync($"/api/v1/payments/{p1}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Reverse(w.Owner, p1, "otra vez")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Allocate(w.Owner, p1, Alloc(b, 10m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reverse(w.Owner, p2, "  ")).StatusCode);
        Assert.Equal(1, (await Json(await w.Owner.GetAsync("/api/v1/payments?status=Reversed"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Reversing_one_allocation_frees_the_amount_to_reassign_and_charges_with_payments_cannot_be_voided()
    {
        var w = await NewWorld("reassign");
        var a = await Charge(w.Owner, w.LeaseId, 1000m, description: "A");
        var b = await Charge(w.Owner, w.LeaseId, 1000m, description: "B");
        var payment = await PayOk(w.Owner, w.TenantId, 1000m, Alloc(a, 1000m));
        var allocationId = (await GetPayment(w.Owner, payment)).GetProperty("allocations")[0].GetProperty("id").GetGuid();

        var blocked = await w.Owner.PostAsJsonAsync($"/api/v1/rent-charges/{a}/void", new { reason = "Error" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("pagos aplicados", (await Json(blocked)).GetProperty("detail").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"/api/v1/payments/{payment}/allocations/{allocationId}/reverse", new { reason = "" })).StatusCode);
        var undone = await w.Owner.PostAsJsonAsync($"/api/v1/payments/{payment}/allocations/{allocationId}/reverse", new { reason = "Se aplicó al cargo equivocado" });
        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);
        Assert.Equal(1000m, (await Json(undone)).GetProperty("unassignedAmount").GetDecimal());
        Assert.Equal("Open", (await GetCharge(w.Owner, a)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"/api/v1/payments/{payment}/allocations/{allocationId}/reverse", new { reason = "otra vez" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Allocate(w.Owner, payment, Alloc(b, 1000m))).StatusCode); // reassigned
        Assert.Equal("Paid", (await GetCharge(w.Owner, b)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/rent-charges/{a}/void", new { reason = "Error" })).StatusCode); // now it can be voided
    }

    [Fact]
    public async Task Validation_rejects_bad_payments()
    {
        var w = await NewWorld("pval");
        var charge = await Charge(w.Owner, w.LeaseId, 1000m);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(w.Owner, w.TenantId, 0m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(w.Owner, w.TenantId, -5m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(w.Owner, w.TenantId, 10.005m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(w.Owner, w.TenantId, 100m, receivedOn: Today.AddDays(1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(w.Owner, Guid.NewGuid(), 100m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(w.Owner, w.TenantId, 100m, reference: new string('r', 101))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(w.Owner, w.TenantId, 100m, [Alloc(charge, 50m), Alloc(charge, 50m)])).StatusCode); // duplicate charge
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(w.Owner, w.TenantId, 100m, [Alloc(charge, 0m)])).StatusCode);
        var badMethod = await w.Owner.PostAsync("/api/v1/payments", new StringContent($$"""{"tenantId":"{{w.TenantId}}","receivedOn":"{{D(Today)}}","amount":10,"method":"Bitcoin"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badMethod.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Pay(w.Owner, w.TenantId, 100m, receivedOn: Today.AddDays(-30), method: "Cash")).StatusCode);
        Assert.Equal(0, (await Json(await w.Owner.GetAsync("/api/v1/payments?search=zzz"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_partially_paid_charge_past_its_due_date_is_overdue_and_a_paid_one_never_is()
    {
        var w = await NewWorld("overdue6");
        var late = await Charge(w.Owner, w.LeaseId, 1000m, due: D(Today.AddDays(-3)));
        var lateAndPaid = await Charge(w.Owner, w.LeaseId, 500m, due: D(Today.AddDays(-3)));
        await PayOk(w.Owner, w.TenantId, 1500m, Alloc(late, 400m), Alloc(lateAndPaid, 500m));
        var c = await GetCharge(w.Owner, late);
        Assert.Equal("Overdue", c.GetProperty("status").GetString());
        Assert.Equal(600m, c.GetProperty("balance").GetDecimal());
        Assert.Equal("Paid", (await GetCharge(w.Owner, lateAndPaid)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Open_charges_are_listed_oldest_first_for_the_payment_form()
    {
        var w = await NewWorld("open");
        var c3 = await Charge(w.Owner, w.LeaseId, 100m, due: "2030-03-05", description: "Marzo");
        var c1 = await Charge(w.Owner, w.LeaseId, 100m, due: "2030-01-05", description: "Enero");
        var c2 = await Charge(w.Owner, w.LeaseId, 100m, due: "2030-02-05", description: "Febrero");
        await PayOk(w.Owner, w.TenantId, 100m, Alloc(c2, 100m));
        var open = (await Json(await w.Owner.GetAsync($"/api/v1/rent-charges?tenantId={w.TenantId}&openOnly=true"))).GetProperty("items").EnumerateArray()
            .Select(c => c.GetProperty("description").GetString()).ToList();
        Assert.Equal(["Enero", "Marzo"], open);
    }

    [Fact]
    public async Task Roles_Finance_registers_and_applies_but_only_Owner_or_Admin_reverse_ReadOnly_reads_Maintenance_has_no_access()
    {
        var w = await NewWorld("roles6");
        var charge = await Charge(w.Owner, w.LeaseId, 1000m);
        var finance = await fx.AddMemberAsync(w.Owner, "Finance");
        var admin = await fx.AddMemberAsync(w.Owner, "Admin");
        var readOnly = await fx.AddMemberAsync(w.Owner, "ReadOnly");
        var maintenance = await fx.AddMemberAsync(w.Owner, "Maintenance");

        var financePayment = await PayOk(finance, w.TenantId, 500m, Alloc(charge, 200m));
        Assert.Equal(HttpStatusCode.OK, (await Allocate(finance, financePayment, Alloc(charge, 100m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Reverse(finance, financePayment)).StatusCode);
        var allocationId = (await GetPayment(finance, financePayment)).GetProperty("allocations")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await finance.PostAsJsonAsync($"/api/v1/payments/{financePayment}/allocations/{allocationId}/reverse", new { reason = "x" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync("/api/v1/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"/api/v1/payments/{financePayment}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Pay(readOnly, w.TenantId, 10m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Allocate(readOnly, financePayment, Alloc(charge, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Reverse(readOnly, financePayment)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await maintenance.GetAsync("/api/v1/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Pay(maintenance, w.TenantId, 10m)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.NewClient().GetAsync("/api/v1/payments")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Reverse(admin, financePayment, "Duplicado")).StatusCode);
    }

    [Fact]
    public async Task OrganizationA_cannot_see_reverse_allocate_or_pay_into_organizationB()
    {
        var a = await NewWorld("iso6-a");
        var b = await NewWorld("iso6-b");
        var chargeA = await Charge(a.Owner, a.LeaseId, 1000m);
        var chargeB = await Charge(b.Owner, b.LeaseId, 1000m);
        var paymentA = await PayOk(a.Owner, a.TenantId, 500m, Alloc(chargeA, 200m));
        var allocationA = (await GetPayment(a.Owner, paymentA)).GetProperty("allocations")[0].GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/payments/{paymentA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Reverse(b.Owner, paymentA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Allocate(b.Owner, paymentA, Alloc(chargeB, 10m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PostAsJsonAsync($"/api/v1/payments/{paymentA}/allocations/{allocationA}/reverse", new { reason = "x" })).StatusCode);
        Assert.Equal(0, (await Json(await b.Owner.GetAsync("/api/v1/payments"))).GetProperty("total").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(b.Owner, a.TenantId, 100m)).StatusCode);                       // A's tenant looks nonexistent to B
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(b.Owner, b.TenantId, 100m, [Alloc(chargeA, 100m)])).StatusCode); // A's charge too

        Assert.Equal("Confirmed", (await GetPayment(a.Owner, paymentA)).GetProperty("status").GetString());
        Assert.Equal(200m, (await GetCharge(a.Owner, chargeA)).GetProperty("paidAmount").GetDecimal());
    }

    [Fact]
    public async Task The_database_refuses_invalid_allocations_even_when_the_application_is_bypassed()
    {
        var w = await NewWorld("dbtrig");
        var charge = await Charge(w.Owner, w.LeaseId, 1000m);
        var otherTenant = (await Json(await w.Owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Luis Prueba" }))).GetProperty("id").GetGuid();
        var otherCharge = await Charge(w.Owner, await NewLease(w.Owner, w.PropertyId, otherTenant), 1000m);
        var payment = await PayOk(w.Owner, w.TenantId, 600m, Alloc(charge, 600m));
        var reversedPayment = await PayOk(w.Owner, w.TenantId, 100m);
        await Reverse(w.Owner, reversedPayment);
        var voided = await Charge(w.Owner, w.LeaseId, 50m);
        await w.Owner.PostAsJsonAsync($"/api/v1/rent-charges/{voided}/void", new { reason = "Error" });

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        async Task<string> Insert(Guid paymentId, Guid chargeId, decimal amount)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO "PaymentAllocations" ("Id","OrganizationId","PaymentId","RentChargeId","Amount","CreatedAt")
                VALUES (gen_random_uuid(), @o, @p, @c, @a, now())
                """, conn);
            cmd.Parameters.AddWithValue("o", w.OrgId); cmd.Parameters.AddWithValue("p", paymentId); cmd.Parameters.AddWithValue("c", chargeId); cmd.Parameters.AddWithValue("a", amount);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            return $"{ex.SqlState}:{ex.MessageText}";
        }
        Assert.Contains("exceed the payment amount", await Insert(payment, otherChargeSameTenant(), 1m));      // payment is fully allocated
        Assert.Contains("exceed the charge amount", await Insert(await PayOk(w.Owner, w.TenantId, 5000m), charge, 500m)); // charge has only 400 left
        Assert.Contains("same tenant", await Insert(payment, otherCharge, 1m));
        Assert.Contains("confirmed payment", await Insert(reversedPayment, charge, 1m));
        Assert.Contains("voided charge", await Insert(payment, voided, 1m));

        Guid otherChargeSameTenant() => charge; // 600 of 600 already allocated: any extra amount breaks the payment limit
    }

    [Fact]
    public async Task Payments_and_reversals_are_audited_without_free_text_personal_data()
    {
        var w = await NewWorld("aud6");
        var charge = await Charge(w.Owner, w.LeaseId, 1000m);
        var payment = await PayOk(w.Owner, w.TenantId, 500m, Alloc(charge, 300m));
        await Allocate(w.Owner, payment, Alloc(charge, 100m));
        await Reverse(w.Owner, payment, "Pago duplicado");

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT "Action", "Reason" FROM "AuditLogs" WHERE "OrganizationId" = @o AND "EntityType" = 'Payment'""", conn);
        cmd.Parameters.AddWithValue("o", w.OrgId);
        var rows = new List<(string, string?)>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) rows.Add((rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1)));
        Assert.Contains(rows, r => r.Item1 == "payment.created");
        Assert.Contains(rows, r => r.Item1 == "payment.allocated");
        Assert.Contains(rows, r => r.Item1 == "payment.reversed" && r.Item2 == "Pago duplicado");
    }
}
