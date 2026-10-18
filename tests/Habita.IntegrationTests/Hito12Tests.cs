using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Habita.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Habita.IntegrationTests;

/// <summary>Hito 12: bank accounts, payment reports with proof, payment promises and the cash QR.</summary>
public class Hito12Tests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    static string D(DateOnly d) => d.ToString("yyyy-MM-dd");
    static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City")).Date);
    const string BadCode = "Código inválido o vencido.";

    /// <summary>A CLABE with a correct check digit: bank 072, then ten digits taken from n.</summary>
    static string Clabe(int n)
    {
        var first17 = "0721800" + n.ToString("D10");
        int[] w = [3, 7, 1]; var sum = 0;
        for (var i = 0; i < 17; i++) sum += ((first17[i] - '0') * w[i % 3]) % 10;
        return first17 + ((10 - sum % 10) % 10);
    }

    // ---------- scaffolding ----------

    record World(HttpClient Owner, HttpClient A, HttpClient B, Guid TenantA, Guid TenantB, Guid LeaseA, Guid LeaseB, Guid PropertyId);

    int _unit;
    async Task<Guid> Lease(HttpClient owner, Guid propertyId, Guid tenantId)
    {
        var uid = (await Json(await owner.PostAsJsonAsync($"/api/v1/properties/{propertyId}/units", new { code = $"K{Interlocked.Increment(ref _unit)}", baseRent = 1000 }))).GetProperty("id").GetGuid();
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

    async Task<Guid> NewProperty(HttpClient owner, string name) =>
        (await Json(await owner.PostAsJsonAsync("/api/v1/properties", new { name, address = "Reforma 100" }))).GetProperty("id").GetGuid();

    async Task<World> NewWorld(string tag)
    {
        var (owner, _, _) = await fx.RegisterAsync(tag);
        var pid = await NewProperty(owner, "Torre Norte");
        var ta = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Ana Ejemplo", email = "ana@example.com" }))).GetProperty("id").GetGuid();
        var tb = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Luis Prueba", email = "luis@example.com" }))).GetProperty("id").GetGuid();
        var la = await Lease(owner, pid, ta); var lb = await Lease(owner, pid, tb);
        return new(owner, await Login(owner, ta), await Login(owner, tb), ta, tb, la, lb, pid);
    }

    static async Task<Guid> Charge(HttpClient c, Guid leaseId, decimal amount, DateOnly due, string description = "Renta")
    {
        var r = await c.PostAsJsonAsync("/api/v1/rent-charges", new { leaseId, description, amount, dueDate = D(due) });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    static async Task<Guid> Pay(HttpClient c, Guid tenantId, decimal amount, params (Guid Charge, decimal Amount)[] allocs)
    {
        var r = await c.PostAsJsonAsync("/api/v1/payments", new
        {
            tenantId, receivedOn = D(Today), amount, method = "BankTransfer",
            allocations = allocs.Select(a => new { chargeId = a.Charge, amount = a.Amount }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    static MultipartFormDataContent FileContent(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        part.Headers.TryAddWithoutValidation("Content-Disposition", "form-data; name=\"file\"; filename=\"comprobante.png\"");
        return new MultipartFormDataContent { part };
    }

    /// <summary>The same seed gives the same bytes (so the same SHA-256); different seeds give different proofs.</summary>
    static byte[] Png(int seed) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, (byte)seed, (byte)(seed >> 8), 1, 2, 3, 4, 5, 6];

    async Task<Guid> Upload(HttpClient c, int seed)
    {
        var r = await c.PostAsync("/api/v1/files", FileContent(Png(seed)));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    int _seed = 1000;
    async Task<HttpResponseMessage> Report(HttpClient tenant, decimal amount = 1000m, int? seed = null, Guid? fileId = null, object? overrides = null)
    {
        var file = fileId ?? await Upload(tenant, seed ?? Interlocked.Increment(ref _seed));
        var body = overrides ?? new { amount, paidOn = D(Today), method = "BankTransfer", reference = "SPEI-1", note = "Pago de octubre", fileId = file };
        return await tenant.PostAsJsonAsync("/api/v1/portal/payment-claims", body);
    }

    async Task<Guid> ReportOk(HttpClient tenant, decimal amount = 1000m, int? seed = null)
    {
        var r = await Report(tenant, amount, seed);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    /// <summary>Moves a cash code's expiry into the past. The trigger forbids it (that is the point), so the test switches it off for this one statement.</summary>
    async Task ExpireToken(Guid tokenId)
    {
        await Sql("ALTER TABLE \"CashTokens\" DISABLE TRIGGER cash_tokens_protect_trg");
        try { await Sql("UPDATE \"CashTokens\" SET \"ExpiresAt\" = now() - interval '1 minute' WHERE \"Id\" = {0}", tokenId); }
        finally { await Sql("ALTER TABLE \"CashTokens\" ENABLE TRIGGER cash_tokens_protect_trg"); }
    }

    async Task Sql(string sql, params object[] args)
    {
        using var scope = fx.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<HabitaDbContext>().Database.ExecuteSqlRawAsync(sql, args);
    }

    static async Task<int> PaymentCount(HttpClient owner, Guid tenantId) =>
        (await Json(await owner.GetAsync($"/api/v1/payments?tenantId={tenantId}"))).GetProperty("total").GetInt32();

    // ---------- bank accounts ----------

    [Fact]
    public async Task The_first_general_account_becomes_the_default_and_the_bank_is_detected()
    {
        var (owner, _, _) = await fx.RegisterAsync("bank");
        var first = await owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "  Alba Arrendadora ", clabe = "012 180 0011 2345 6781" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var a = await Json(first);
        Assert.Equal("BBVA México", a.GetProperty("bankName").GetString());
        Assert.Equal("012180001123456781", a.GetProperty("clabe").GetString());
        Assert.Equal("Alba Arrendadora", a.GetProperty("holderName").GetString());
        Assert.True(a.GetProperty("isDefault").GetBoolean());

        var second = await Json(await owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "Alba", clabe = Clabe(1) }));
        Assert.False(second.GetProperty("isDefault").GetBoolean()); // there already was a default
        Assert.Equal("Banorte", second.GetProperty("bankName").GetString());

        var promote = await owner.PatchAsJsonAsync($"/api/v1/bank-accounts/{second.GetProperty("id").GetGuid()}", new { isDefault = true });
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        var list = (await Json(await owner.GetAsync("/api/v1/bank-accounts"))).EnumerateArray().ToList();
        Assert.Single(list, x => x.GetProperty("isDefault").GetBoolean());
        Assert.True(list.Single(x => x.GetProperty("id").GetGuid() == second.GetProperty("id").GetGuid()).GetProperty("isDefault").GetBoolean());

        // The current default cannot simply be switched off: there would be no account to show tenants.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync($"/api/v1/bank-accounts/{second.GetProperty("id").GetGuid()}", new { isDefault = false })).StatusCode);
    }

    [Fact]
    public async Task Invalid_bank_accounts_are_refused()
    {
        var (owner, _, _) = await fx.RegisterAsync("bank2");
        var (other, _, _) = await fx.RegisterAsync("bank2other");
        var foreignProperty = await NewProperty(other, "Ajena");
        var property = await NewProperty(owner, "Propia");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "X", clabe = "012180001123456782" })).StatusCode); // check digit
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "X", clabe = "123" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = " ", clabe = Clabe(2) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = new string('x', 201), clabe = Clabe(2) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "X", clabe = Clabe(2), propertyId = property, isDefault = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "X", clabe = Clabe(2), propertyId = foreignProperty })).StatusCode);
        Assert.Equal(0, (await Json(await owner.GetAsync("/api/v1/bank-accounts"))).GetArrayLength()); // nothing was saved
    }

    [Fact]
    public async Task A_property_account_wins_over_the_default_for_that_propertys_tenants()
    {
        var w = await NewWorld("bankprop");
        var general = await Json(await w.Owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "General", clabe = Clabe(10) }));
        var other = await NewProperty(w.Owner, "Torre Sur");
        var tc = (await Json(await w.Owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Carla Sur" }))).GetProperty("id").GetGuid();
        await Lease(w.Owner, other, tc);
        var carla = await Login(w.Owner, tc);

        var own = await w.Owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "Torre Norte SA", clabe = Clabe(11), propertyId = w.PropertyId });
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        var ownJson = await Json(own);
        Assert.False(ownJson.GetProperty("isDefault").GetBoolean());
        Assert.Equal("Torre Norte", ownJson.GetProperty("propertyName").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "Otra", clabe = Clabe(12), propertyId = w.PropertyId })).StatusCode);

        Assert.Equal(Clabe(11), (await Json(await w.A.GetAsync("/api/v1/portal/payment-info"))).GetProperty("account").GetProperty("clabe").GetString());
        Assert.Equal(Clabe(10), (await Json(await carla.GetAsync("/api/v1/portal/payment-info"))).GetProperty("account").GetProperty("clabe").GetString());

        // Archived accounts stop being shown; the default is what is left.
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsync($"/api/v1/bank-accounts/{ownJson.GetProperty("id").GetGuid()}/archive", null)).StatusCode);
        Assert.Equal(Clabe(10), (await Json(await w.A.GetAsync("/api/v1/portal/payment-info"))).GetProperty("account").GetProperty("clabe").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PatchAsJsonAsync($"/api/v1/bank-accounts/{ownJson.GetProperty("id").GetGuid()}", new { holderName = "Tarde" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "Nueva Torre Norte", clabe = Clabe(13), propertyId = w.PropertyId })).StatusCode == HttpStatusCode.Created ? HttpStatusCode.OK : HttpStatusCode.BadRequest); // the slot is free again

        // The only account left cannot be archived out from under the default rule when others exist, but a lone default can go.
        var second = await Json(await w.Owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "Segunda", clabe = Clabe(14) }));
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsync($"/api/v1/bank-accounts/{general.GetProperty("id").GetGuid()}/archive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsync($"/api/v1/bank-accounts/{second.GetProperty("id").GetGuid()}/archive", null)).StatusCode);
    }

    [Fact]
    public async Task Without_any_account_the_tenant_gets_an_empty_answer_not_an_error()
    {
        var w = await NewWorld("bankless");
        var info = await Json(await w.A.GetAsync("/api/v1/portal/payment-info"));
        Assert.Equal(JsonValueKind.Null, info.GetProperty("account").ValueKind);
    }

    [Fact]
    public async Task Only_one_default_survives_accounts_created_at_the_same_time()
    {
        var (owner, _, _) = await fx.RegisterAsync("bankrace");
        var results = await Task.WhenAll(Enumerable.Range(20, 8).Select(i => owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = $"Cuenta {i}", clabe = Clabe(i), isDefault = true })));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var list = (await Json(await owner.GetAsync("/api/v1/bank-accounts"))).EnumerateArray().ToList();
        Assert.Equal(8, list.Count);
        Assert.Single(list, x => x.GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task Bank_accounts_are_managed_by_owner_and_admin_and_read_by_those_who_read_reports()
    {
        var (owner, _, _) = await fx.RegisterAsync("bankroles");
        var account = (await Json(await owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "Alba", clabe = Clabe(30) }))).GetProperty("id").GetGuid();
        var admin = await fx.AddMemberAsync(owner, "Admin");
        var finance = await fx.AddMemberAsync(owner, "Finance");
        var readOnly = await fx.AddMemberAsync(owner, "ReadOnly");
        var collector = await fx.AddMemberAsync(owner, "Collector");
        var maintenance = await fx.AddMemberAsync(owner, "Maintenance");

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "Otra", clabe = Clabe(31) })).StatusCode);
        foreach (var reader in new[] { finance, readOnly }) Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/bank-accounts")).StatusCode);
        foreach (var blocked in new[] { collector, maintenance }) Assert.Equal(HttpStatusCode.Forbidden, (await blocked.GetAsync("/api/v1/bank-accounts")).StatusCode);
        foreach (var c in new[] { finance, readOnly, collector, maintenance })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "X", clabe = Clabe(32) })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PatchAsJsonAsync($"/api/v1/bank-accounts/{account}", new { holderName = "X" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"/api/v1/bank-accounts/{account}/archive", null)).StatusCode);
        }
    }

    [Fact]
    public async Task Bank_accounts_do_not_cross_organizations_and_the_audit_never_holds_the_full_clabe()
    {
        var (owner, _, _) = await fx.RegisterAsync("bankiso");
        var (other, _, _) = await fx.RegisterAsync("bankiso2");
        var id = (await Json(await owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "Alba", clabe = Clabe(40) }))).GetProperty("id").GetGuid();
        Assert.Equal(0, (await Json(await other.GetAsync("/api/v1/bank-accounts"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await other.PatchAsJsonAsync($"/api/v1/bank-accounts/{id}", new { holderName = "Robo" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/bank-accounts/{id}/archive", null)).StatusCode);

        var audit = await (await owner.GetAsync("/api/v1/audit-logs?entityType=BankAccount")).Content.ReadAsStringAsync();
        Assert.Contains("bank_account.created", audit);
        Assert.DoesNotContain(Clabe(40), audit);
        Assert.Contains(Clabe(40)[^4..], audit);
    }

    // ---------- payment reports ----------

    [Fact]
    public async Task A_tenant_reports_a_payment_and_staff_confirm_it_into_a_real_payment()
    {
        var w = await NewWorld("claim");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector");
        var charge = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-3));
        var claimId = await ReportOk(w.A, 1000m);

        var mine = await Json(await w.A.GetAsync("/api/v1/portal/payment-claims"));
        Assert.Equal(1, mine.GetProperty("total").GetInt32());
        Assert.Equal("Pending", mine.GetProperty("items")[0].GetProperty("status").GetString());

        // Staff see it in the queue, with the tenant and the proof, and can open the proof.
        var queue = await Json(await collector.GetAsync("/api/v1/payment-claims?status=Pending"));
        Assert.Equal(1, queue.GetProperty("total").GetInt32());
        var item = queue.GetProperty("items")[0];
        Assert.Equal("Ana Ejemplo", item.GetProperty("tenantName").GetString());
        Assert.Equal("SPEI-1", item.GetProperty("reference").GetString());
        var fileId = item.GetProperty("file").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await collector.GetAsync($"/api/v1/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.B.GetAsync($"/api/v1/files/{fileId}")).StatusCode); // another tenant cannot open it
        Assert.Equal(HttpStatusCode.OK, (await w.A.GetAsync($"/api/v1/files/{fileId}")).StatusCode);

        var confirm = await collector.PostAsJsonAsync($"/api/v1/payment-claims/{claimId}/confirm", new { allocations = new[] { new { chargeId = charge, amount = 1000m } } });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var c = await Json(confirm);
        Assert.Equal("Confirmed", c.GetProperty("status").GetString());
        var paymentId = c.GetProperty("paymentId").GetGuid();

        var payment = await Json(await w.Owner.GetAsync($"/api/v1/payments/{paymentId}"));
        Assert.Equal("BankTransfer", payment.GetProperty("method").GetString());
        Assert.Equal("SPEI-1", payment.GetProperty("reference").GetString());
        Assert.Equal(1000m, payment.GetProperty("amount").GetDecimal());
        Assert.Equal(D(Today), payment.GetProperty("receivedOn").GetString());
        Assert.Contains("Pago de octubre", payment.GetProperty("notes").GetString());
        Assert.Equal(1, payment.GetProperty("allocations").GetArrayLength());
        Assert.Equal("Paid", (await Json(await w.Owner.GetAsync($"/api/v1/rent-charges/{charge}"))).GetProperty("status").GetString());
        Assert.Equal(0m, (await Json(await w.A.GetAsync("/api/v1/portal/summary"))).GetProperty("totalDue").GetDecimal());
        Assert.Equal("Confirmed", (await Json(await w.A.GetAsync("/api/v1/portal/payment-claims"))).GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal(0, (await Json(await collector.GetAsync("/api/v1/payment-claims?status=Pending"))).GetProperty("total").GetInt32());

        var audit = await (await w.Owner.GetAsync($"/api/v1/audit-logs?entityId={claimId}")).Content.ReadAsStringAsync();
        Assert.Contains("payment_claim.created", audit); Assert.Contains("payment_claim.confirmed", audit);
    }

    [Fact]
    public async Task Confirming_without_allocations_keeps_the_money_as_credit()
    {
        var w = await NewWorld("claimcredit");
        var claimId = await ReportOk(w.A, 750m);
        var confirm = await w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{claimId}/confirm", new { });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var payment = await Json(await w.Owner.GetAsync($"/api/v1/payments/{(await Json(confirm)).GetProperty("paymentId").GetGuid()}"));
        Assert.Equal(750m, payment.GetProperty("unassignedAmount").GetDecimal());
        Assert.Equal(750m, (await Json(await w.A.GetAsync("/api/v1/portal/summary"))).GetProperty("creditBalance").GetDecimal());
    }

    [Fact]
    public async Task A_confirmation_that_cannot_be_applied_changes_nothing()
    {
        var w = await NewWorld("claimfail");
        var mine = await Charge(w.Owner, w.LeaseA, 500m, Today.AddDays(-3));
        var theirs = await Charge(w.Owner, w.LeaseB, 500m, Today.AddDays(-3));
        var claimId = await ReportOk(w.A, 1000m);

        var tooMuch = await w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{claimId}/confirm", new { allocations = new[] { new { chargeId = mine, amount = 600m } } });
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        var wrongTenant = await w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{claimId}/confirm", new { allocations = new[] { new { chargeId = theirs, amount = 100m } } });
        Assert.Equal(HttpStatusCode.BadRequest, wrongTenant.StatusCode);
        var overClaim = await w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{claimId}/confirm", new { allocations = new[] { new { chargeId = mine, amount = 500m }, new { chargeId = Guid.NewGuid(), amount = 600m } } });
        Assert.True(overClaim.StatusCode is HttpStatusCode.BadRequest);

        Assert.Equal("Pending", (await Json(await w.Owner.GetAsync($"/api/v1/payment-claims/{claimId}"))).GetProperty("status").GetString());
        Assert.Equal(0, await PaymentCount(w.Owner, w.TenantA));
        // And a correct confirmation still works afterwards.
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{claimId}/confirm", new { allocations = new[] { new { chargeId = mine, amount = 500m } } })).StatusCode);
    }

    [Fact]
    public async Task Rejecting_needs_a_comment_and_the_tenant_reads_it_without_seeing_staff_details()
    {
        var w = await NewWorld("claimrej");
        var claimId = await ReportOk(w.A, 1000m);
        foreach (var bad in new object[] { new { }, new { comment = "" }, new { comment = "   " }, new { comment = new string('x', 501) } })
            Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{claimId}/reject", bad)).StatusCode);

        var rej = await w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{claimId}/reject", new { comment = "El monto no coincide con el banco" });
        Assert.Equal(HttpStatusCode.OK, rej.StatusCode);
        var staffView = await Json(rej);
        Assert.Equal("Rejected", staffView.GetProperty("status").GetString());
        Assert.Equal("Usuario Prueba", staffView.GetProperty("reviewedByName").GetString());

        var raw = await (await w.A.GetAsync("/api/v1/portal/payment-claims")).Content.ReadAsStringAsync();
        Assert.Contains("El monto no coincide con el banco", raw);
        Assert.DoesNotContain("Usuario Prueba", raw); Assert.DoesNotContain("reviewedBy", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("paymentId", raw, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("tenantName", raw, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, await PaymentCount(w.Owner, w.TenantA));
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{claimId}/confirm", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{claimId}/reject", new { comment = "otra vez" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.A.PostAsync($"/api/v1/portal/payment-claims/{claimId}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Two_people_confirming_the_same_report_create_exactly_one_payment()
    {
        var w = await NewWorld("claimrace");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector");
        var charge = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-3));
        var claimId = await ReportOk(w.A, 1000m);
        var clients = new[] { w.Owner, collector };
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            clients[i % 2].PostAsJsonAsync($"/api/v1/payment-claims/{claimId}/confirm", new { allocations = new[] { new { chargeId = charge, amount = 1000m } } })));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await PaymentCount(w.Owner, w.TenantA));
        Assert.Equal("Paid", (await Json(await w.Owner.GetAsync($"/api/v1/rent-charges/{charge}"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Confirm_and_reject_racing_each_other_leave_a_consistent_result()
    {
        var w = await NewWorld("claimmix");
        var claimId = await ReportOk(w.A, 400m);
        var tasks = Enumerable.Range(0, 6).Select(i => i % 2 == 0
            ? w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{claimId}/confirm", new { })
            : w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{claimId}/reject", new { comment = "No" }));
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        var final = await Json(await w.Owner.GetAsync($"/api/v1/payment-claims/{claimId}"));
        var payments = await PaymentCount(w.Owner, w.TenantA);
        Assert.Equal(final.GetProperty("status").GetString() == "Confirmed" ? 1 : 0, payments); // a rejected report never leaves a payment behind
    }

    [Fact]
    public async Task Reports_follow_the_rules_for_amount_date_method_and_proof()
    {
        var w = await NewWorld("claimrules");
        var file = await Upload(w.A, 5001);
        object Body(object? amount = null, string? paidOn = null, string method = "BankTransfer", string? note = null, Guid? fileId = null) =>
            new { amount = amount ?? 100m, paidOn = paidOn ?? D(Today), method, note, fileId = fileId ?? file };

        foreach (var bad in new[]
        {
            Body(amount: 0m), Body(amount: -5m), Body(amount: 10.005m), Body(amount: 100000000m),
            Body(paidOn: D(Today.AddDays(1))), Body(paidOn: D(Today.AddDays(-400))),
            Body(method: "Card"), Body(note: new string('n', 501)), Body(fileId: Guid.Empty), Body(fileId: Guid.NewGuid()),
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await w.A.PostAsJsonAsync("/api/v1/portal/payment-claims", bad)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.A.PostAsJsonAsync("/api/v1/portal/payment-claims", new { amount = 100m, paidOn = D(Today), method = "Bitcoin", fileId = file })).StatusCode);

        // A proof must be the tenant's own upload; someone else's file looks like a missing one.
        var bsFile = await Upload(w.B, 5002);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.A.PostAsJsonAsync("/api/v1/portal/payment-claims", Body(fileId: bsFile))).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await w.A.PostAsJsonAsync("/api/v1/portal/payment-claims", Body())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.A.PostAsJsonAsync("/api/v1/portal/payment-claims", Body())).StatusCode); // same proof twice
        Assert.Equal(1, (await Json(await w.A.GetAsync("/api/v1/portal/payment-claims"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_tenant_can_have_at_most_five_reports_waiting_for_review()
    {
        var w = await NewWorld("claimcap");
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add(await ReportOk(w.A, 10m + i));
        Assert.Equal(HttpStatusCode.Conflict, (await Report(w.A, 99m)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.A.PostAsync($"/api/v1/portal/payment-claims/{ids[0]}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Report(w.A, 99m)).StatusCode);
        Assert.Equal(0, (await Json(await w.B.GetAsync("/api/v1/portal/payment-claims"))).GetProperty("total").GetInt32()); // B is unaffected
        Assert.Equal(HttpStatusCode.Created, (await Report(w.B, 99m)).StatusCode);
    }

    [Fact]
    public async Task The_cap_holds_when_a_tenant_taps_send_many_times_at_once()
    {
        var w = await NewWorld("claimcaprace");
        var files = new List<Guid>();
        for (var i = 0; i < 9; i++) files.Add(await Upload(w.A, 7000 + i));
        var results = await Task.WhenAll(files.Select(f => Report(w.A, fileId: f)));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(4, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task A_tenant_cancels_only_their_own_pending_report()
    {
        var w = await NewWorld("claimcancel");
        var id = await ReportOk(w.A);
        Assert.Equal(HttpStatusCode.NotFound, (await w.B.PostAsync($"/api/v1/portal/payment-claims/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.A.PostAsync($"/api/v1/portal/payment-claims/{Guid.NewGuid()}/cancel", null)).StatusCode);
        var cancelled = await w.A.PostAsync($"/api/v1/portal/payment-claims/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("Cancelled", (await Json(cancelled)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await w.A.PostAsync($"/api/v1/portal/payment-claims/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{id}/confirm", new { })).StatusCode);
    }

    [Fact]
    public async Task The_same_proof_in_two_reports_is_flagged_for_staff()
    {
        var w = await NewWorld("claimdup");
        var a = await ReportOk(w.A, 100m, seed: 9001);
        var unique = await ReportOk(w.A, 100m, seed: 9002);
        var b = await ReportOk(w.B, 100m, seed: 9001); // identical bytes, uploaded separately
        Assert.True((await Json(await w.Owner.GetAsync($"/api/v1/payment-claims/{a}"))).GetProperty("duplicateProof").GetBoolean());
        Assert.True((await Json(await w.Owner.GetAsync($"/api/v1/payment-claims/{b}"))).GetProperty("duplicateProof").GetBoolean());
        Assert.False((await Json(await w.Owner.GetAsync($"/api/v1/payment-claims/{unique}"))).GetProperty("duplicateProof").GetBoolean());
    }

    [Fact]
    public async Task Payment_reports_are_open_to_the_roles_that_record_payments_only()
    {
        var w = await NewWorld("claimroles");
        var id = await ReportOk(w.A);
        foreach (var role in new[] { "Finance", "Collector", "Admin" })
        {
            var c = await fx.AddMemberAsync(w.Owner, role);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/payment-claims")).StatusCode);
        }
        foreach (var role in new[] { "Maintenance", "ReadOnly" })
        {
            var c = await fx.AddMemberAsync(w.Owner, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/payment-claims")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/v1/payment-claims/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync($"/api/v1/payment-claims/{id}/confirm", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync($"/api/v1/payment-claims/{id}/reject", new { comment = "x" })).StatusCode);
        }
        // A tenant has the portal routes, never the staff ones.
        Assert.Equal(HttpStatusCode.Forbidden, (await w.A.GetAsync("/api/v1/payment-claims")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.A.PostAsJsonAsync($"/api/v1/payment-claims/{id}/confirm", new { })).StatusCode);
    }

    [Fact]
    public async Task Payment_reports_do_not_cross_organizations()
    {
        var w = await NewWorld("claimiso");
        var (other, _, _) = await fx.RegisterAsync("claimiso2");
        var id = await ReportOk(w.A);
        Assert.Equal(0, (await Json(await other.GetAsync("/api/v1/payment-claims"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/payment-claims/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/v1/payment-claims/{id}/confirm", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/v1/payment-claims/{id}/reject", new { comment = "x" })).StatusCode);
        Assert.Equal("Pending", (await Json(await w.Owner.GetAsync($"/api/v1/payment-claims/{id}"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_reviewed_report_can_never_change_or_disappear_in_the_database()
    {
        var w = await NewWorld("claimdb");
        var done = await ReportOk(w.A, 100m);
        var pending = await ReportOk(w.A, 200m);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{done}/confirm", new { })).StatusCode);

        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"PaymentClaims\" SET \"Amount\" = 1 WHERE \"Id\" = {0}", done));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"PaymentClaims\" SET \"Status\" = 'Pending', \"PaymentId\" = NULL WHERE \"Id\" = {0}", done));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("DELETE FROM \"PaymentClaims\" WHERE \"Id\" = {0}", done));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("DELETE FROM \"PaymentClaims\" WHERE \"Id\" = {0}", pending));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"PaymentClaims\" SET \"Amount\" = 1 WHERE \"Id\" = {0}", pending)); // the amount staff are reviewing is fixed too
        Assert.Equal(100m, (await Json(await w.Owner.GetAsync($"/api/v1/payment-claims/{done}"))).GetProperty("amount").GetDecimal());
    }

    // ---------- payment promises ----------

    async Task<JsonElement> Promises(HttpClient c, string query = "") => await Json(await c.GetAsync($"/api/v1/payment-promises{query}"));

    [Fact]
    public async Task A_promise_is_kept_when_the_charge_is_paid_by_the_promised_day()
    {
        var w = await NewWorld("promkept");
        var charge = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-2));
        var created = await w.Owner.PostAsJsonAsync("/api/v1/payment-promises", new { chargeId = charge, promisedOn = D(Today.AddDays(5)), note = "Cobra el viernes" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var p = await Json(created);
        Assert.Equal("Pending", p.GetProperty("status").GetString());
        Assert.Equal("Ana Ejemplo", p.GetProperty("tenantName").GetString());
        Assert.Equal(1000m, p.GetProperty("chargeBalance").GetDecimal());

        await Pay(w.Owner, w.TenantA, 400m, (charge, 400m));
        Assert.Equal("Pending", (await Promises(w.Owner, $"?chargeId={charge}")).GetProperty("items")[0].GetProperty("status").GetString());
        await Pay(w.Owner, w.TenantA, 600m, (charge, 600m));
        var after = (await Promises(w.Owner, $"?chargeId={charge}")).GetProperty("items")[0];
        Assert.Equal("Kept", after.GetProperty("status").GetString());
        Assert.Equal(0m, after.GetProperty("chargeBalance").GetDecimal());
        Assert.Equal(1, (await Promises(w.Owner, "?status=Kept")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_promise_is_broken_when_the_day_passes_without_the_money()
    {
        var w = await NewWorld("prombroken");
        var late = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-9), "Renta tarde");
        var never = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-9), "Renta nunca");
        var ok = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-9), "Renta puntual");
        foreach (var c in new[] { late, never, ok })
            Assert.Equal(HttpStatusCode.Created, (await w.Owner.PostAsJsonAsync("/api/v1/payment-promises", new { chargeId = c, promisedOn = D(Today.AddDays(2)) })).StatusCode);
        await Pay(w.Owner, w.TenantA, 1000m, (ok, 1000m)); // paid today, before the promised day
        await Pay(w.Owner, w.TenantA, 1000m, (late, 1000m)); // also paid today...

        // ...but "late" had promised yesterday, so today's money came too late. Time is moved by rewriting the promised date (no clock to travel with).
        await Sql("UPDATE \"PaymentPromises\" SET \"PromisedOn\" = {0} WHERE \"ChargeId\" IN ({1}, {2})", Today.AddDays(-1), late, never);
        var all = (await Promises(w.Owner)).GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("chargeId").GetGuid(), x => x.GetProperty("status").GetString());
        Assert.Equal("Broken", all[late]);
        Assert.Equal("Broken", all[never]);
        Assert.Equal("Kept", all[ok]);
        Assert.Equal(2, (await Promises(w.Owner, "?status=Broken")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_new_promise_replaces_the_open_one_for_the_same_charge()
    {
        var w = await NewWorld("promreplace");
        var charge = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-2));
        for (var i = 1; i <= 3; i++)
            Assert.Equal(HttpStatusCode.Created, (await w.Owner.PostAsJsonAsync("/api/v1/payment-promises", new { chargeId = charge, promisedOn = D(Today.AddDays(i)) })).StatusCode);
        var items = (await Promises(w.Owner, $"?chargeId={charge}")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(1, items.Count(x => x.GetProperty("status").GetString() == "Pending"));
        Assert.Equal(2, items.Count(x => x.GetProperty("status").GetString() == "Cancelled"));
    }

    [Fact]
    public async Task Promises_made_at_the_same_time_leave_a_single_open_one()
    {
        var w = await NewWorld("promrace");
        var charge = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-2));
        var results = await Task.WhenAll(Enumerable.Range(1, 8).Select(i => w.Owner.PostAsJsonAsync("/api/v1/payment-promises", new { chargeId = charge, promisedOn = D(Today.AddDays(i)) })));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(1, (await Promises(w.Owner, $"?chargeId={charge}&status=Pending")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Promise_rules_dates_charges_and_cancellation()
    {
        var w = await NewWorld("promrules");
        var (other, _, _) = await fx.RegisterAsync("promrules2");
        var charge = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-2));
        var paid = await Charge(w.Owner, w.LeaseA, 300m, Today.AddDays(-2));
        await Pay(w.Owner, w.TenantA, 300m, (paid, 300m));
        var voided = await Charge(w.Owner, w.LeaseA, 300m, Today.AddDays(-2));
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/rent-charges/{voided}/void", new { reason = "Error de captura" })).StatusCode);

        Task<HttpResponseMessage> Make(Guid c, DateOnly on, string? note = null) => w.Owner.PostAsJsonAsync("/api/v1/payment-promises", new { chargeId = c, promisedOn = D(on), note });
        Assert.Equal(HttpStatusCode.BadRequest, (await Make(charge, Today.AddDays(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Make(charge, Today.AddDays(91))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Make(charge, Today.AddDays(2), new string('x', 501))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Make(paid, Today.AddDays(2))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Make(voided, Today.AddDays(2))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Make(Guid.NewGuid(), Today.AddDays(2))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/api/v1/payment-promises", new { chargeId = charge, promisedOn = D(Today.AddDays(2)) })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Make(charge, Today)).StatusCode); // today and day 90 are allowed
        Assert.Equal(HttpStatusCode.Created, (await Make(charge, Today.AddDays(90))).StatusCode);

        var open = (await Promises(w.Owner, "?status=Pending")).GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/v1/payment-promises/{open}/cancel", new { reason = "x" })).StatusCode);
        var cancel = await w.Owner.PostAsJsonAsync($"/api/v1/payment-promises/{open}/cancel", new { reason = "El inquilino cambió de plan" });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("Cancelled", (await Json(cancel)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"/api/v1/payment-promises/{open}/cancel", new { reason = "otra vez" })).StatusCode);
    }

    [Fact]
    public async Task Promises_follow_the_roles_and_stay_inside_the_organization()
    {
        var w = await NewWorld("promroles");
        var (other, _, _) = await fx.RegisterAsync("promroles2");
        var charge = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-2));
        var collector = await fx.AddMemberAsync(w.Owner, "Collector");
        var readOnly = await fx.AddMemberAsync(w.Owner, "ReadOnly");
        var maintenance = await fx.AddMemberAsync(w.Owner, "Maintenance");
        var body = new { chargeId = charge, promisedOn = D(Today.AddDays(3)) };

        Assert.Equal(HttpStatusCode.Created, (await collector.PostAsJsonAsync("/api/v1/payment-promises", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync("/api/v1/payment-promises")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync("/api/v1/payment-promises", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maintenance.GetAsync("/api/v1/payment-promises")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.A.GetAsync("/api/v1/payment-promises")).StatusCode);
        Assert.Equal(0, (await Promises(other)).GetProperty("total").GetInt32());
    }

    // ---------- cash QR ----------

    async Task<JsonElement> CashToken(HttpClient tenant)
    {
        var r = await tenant.PostAsync("/api/v1/portal/cash-token", null);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Json(r);
    }

    static Task<HttpResponseMessage> Resolve(HttpClient staff, string? code) => staff.PostAsJsonAsync("/api/v1/cash-tokens/resolve", new { code });
    static Task<HttpResponseMessage> Collect(HttpClient staff, Guid tokenId, decimal amount, params (Guid Charge, decimal Amount)[] allocs) =>
        staff.PostAsJsonAsync($"/api/v1/cash-tokens/{tokenId}/collect", new { amount, allocations = allocs.Select(a => new { chargeId = a.Charge, amount = a.Amount }).ToArray() });

    [Fact]
    public async Task A_cash_code_shows_the_collector_what_the_tenant_owes_and_one_collection_burns_it()
    {
        var w = await NewWorld("cash");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector");
        var old = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-20), "Renta vieja");
        var recent = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-2), "Renta reciente");
        await Charge(w.Owner, w.LeaseB, 999m, Today.AddDays(-2), "Cargo de Luis");

        var token = await CashToken(w.A);
        var code = token.GetProperty("code").GetString()!;
        Assert.Matches(@"^[2-9A-HJKMNP-Z]{4}-[2-9A-HJKMNP-Z]{4}-[2-9A-HJKMNP-Z]{2}$", code);
        Assert.Equal("habita:cash:" + code, token.GetProperty("qrPayload").GetString());
        var hours = (token.GetProperty("expiresAt").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalHours;
        Assert.InRange(hours, 3.9, 4.1);

        var info = await Json(await Resolve(collector, token.GetProperty("qrPayload").GetString())); // what the camera reads
        Assert.Equal("Ana Ejemplo", info.GetProperty("tenantName").GetString());
        Assert.Equal("MXN", info.GetProperty("currency").GetString());
        var charges = info.GetProperty("charges").EnumerateArray().ToList();
        Assert.Equal(new[] { "Renta vieja", "Renta reciente" }, charges.Select(c => c.GetProperty("description").GetString())); // oldest first, only Ana's
        Assert.Equal("Overdue", charges[0].GetProperty("status").GetString());
        var tokenId = info.GetProperty("tokenId").GetGuid();
        Assert.Equal(tokenId, token.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await Resolve(collector, "  " + code.ToLowerInvariant().Replace("-", " ") + " ")).StatusCode); // typed by hand
        Assert.Equal(0, await PaymentCount(w.Owner, w.TenantA)); // resolving consumes nothing

        var collected = await Collect(collector, tokenId, 1500m, (old, 1000m), (recent, 500m));
        Assert.Equal(HttpStatusCode.Created, collected.StatusCode);
        var payment = await Json(collected);
        Assert.Equal("Cash", payment.GetProperty("method").GetString());
        Assert.Equal("Cobro con QR", payment.GetProperty("reference").GetString());
        Assert.Equal(D(Today), payment.GetProperty("receivedOn").GetString());
        Assert.Equal(1500m, payment.GetProperty("allocatedAmount").GetDecimal());
        Assert.Equal("Paid", (await Json(await w.Owner.GetAsync($"/api/v1/rent-charges/{old}"))).GetProperty("status").GetString());
        var partly = await Json(await w.Owner.GetAsync($"/api/v1/rent-charges/{recent}"));
        Assert.Equal("Overdue", partly.GetProperty("status").GetString()); // part-paid and past due: the derived status is Overdue
        Assert.Equal(500m, partly.GetProperty("balance").GetDecimal());

        // The code is spent: it resolves to nothing. Sending the very same collection again (a dropped connection) replays; anything else is refused.
        Assert.Equal(BadCode, (await Json(await Resolve(collector, code))).GetProperty("detail").GetString());
        var replay = await Collect(collector, tokenId, 1500m, (old, 1000m), (recent, 500m));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(HttpStatusCode.Conflict, (await Collect(collector, tokenId, 200m, (recent, 200m))).StatusCode);
        Assert.Equal(1, await PaymentCount(w.Owner, w.TenantA));

        var audit = await (await w.Owner.GetAsync($"/api/v1/audit-logs?entityId={tokenId}")).Content.ReadAsStringAsync();
        Assert.Contains("cash_token.created", audit); Assert.Contains("cash_token.used", audit);
        Assert.DoesNotContain(code, audit); Assert.DoesNotContain(code.Replace("-", ""), audit);
    }

    [Fact]
    public async Task A_new_code_replaces_the_previous_and_the_tenant_can_cancel_theirs()
    {
        var w = await NewWorld("cashreplace");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector");
        var first = (await CashToken(w.A)).GetProperty("code").GetString();
        var second = (await CashToken(w.A)).GetProperty("code").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await Resolve(collector, first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Resolve(collector, second)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.A.DeleteAsync("/api/v1/portal/cash-token")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Resolve(collector, second)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.A.DeleteAsync("/api/v1/portal/cash-token")).StatusCode); // nothing to cancel is fine
        // Cancelling does not touch the other tenant's code.
        var bs = (await CashToken(w.B)).GetProperty("code").GetString();
        await w.A.DeleteAsync("/api/v1/portal/cash-token");
        Assert.Equal(HttpStatusCode.OK, (await Resolve(collector, bs)).StatusCode);
    }

    [Fact]
    public async Task Every_bad_code_gets_the_same_answer()
    {
        var w = await NewWorld("cashbad");
        var (otherOwner, _, _) = await fx.RegisterAsync("cashbad2");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector");
        var foreignCollector = await fx.AddMemberAsync(otherOwner, "Collector");
        var expired = await CashToken(w.A);
        await ExpireToken(expired.GetProperty("id").GetGuid());
        var live = (await CashToken(w.B)).GetProperty("code").GetString();

        foreach (var (staff, code) in new (HttpClient, string?)[]
        {
            (collector, null), (collector, ""), (collector, "nope"), (collector, "2222-2222-22"), (collector, "https://example.com/pay"),
            (collector, expired.GetProperty("code").GetString()), (foreignCollector, live), // a valid code opened from another organization
        })
        {
            var r = await Resolve(staff, code);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal(BadCode, (await Json(r)).GetProperty("detail").GetString());
        }
        // Collecting with an expired or foreign token id is refused too, and writes nothing.
        Assert.Equal(HttpStatusCode.BadRequest, (await Collect(collector, expired.GetProperty("id").GetGuid(), 100m)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Collect(foreignCollector, expired.GetProperty("id").GetGuid(), 100m)).StatusCode);
        Assert.Equal(0, await PaymentCount(w.Owner, w.TenantA));
    }

    [Fact]
    public async Task Collectors_racing_for_one_code_produce_exactly_one_payment()
    {
        var w = await NewWorld("cashrace");
        var charge = await Charge(w.Owner, w.LeaseA, 5000m, Today.AddDays(-2));
        var collectors = new List<HttpClient>();
        for (var i = 0; i < 3; i++) collectors.Add(await fx.AddMemberAsync(w.Owner, "Collector"));
        var token = await CashToken(w.A);
        var id = token.GetProperty("id").GetGuid();
        // Different amounts, so these are different collections (not retries of one): only one may win.
        var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(i => Collect(collectors[i % 3], id, 100m * i, (charge, 100m * i))));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await PaymentCount(w.Owner, w.TenantA));
    }

    [Fact]
    public async Task The_same_collection_sent_several_times_at_once_creates_one_payment_and_replays_the_rest()
    {
        var w = await NewWorld("cashretry");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector");
        var charge = await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(-2));
        var token = await CashToken(w.A);
        var id = token.GetProperty("id").GetGuid();
        // A flaky connection: the app sends the identical request again while the first is still running. Never an error, never two payments.
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Collect(collector, id, 1000m, (charge, 1000m))));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, await PaymentCount(w.Owner, w.TenantA));
        var audit = JsonDocument.Parse(await (await w.Owner.GetAsync($"/api/v1/audit-logs?entityId={id}&action=cash_token.used")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, audit.GetProperty("total").GetInt32()); // the token was burned once
    }

    [Fact]
    public async Task A_collection_that_breaks_the_payment_rules_leaves_the_code_usable()
    {
        var w = await NewWorld("cashrules");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector");
        var mine = await Charge(w.Owner, w.LeaseA, 500m, Today.AddDays(-2));
        var theirs = await Charge(w.Owner, w.LeaseB, 500m, Today.AddDays(-2));
        var token = await CashToken(w.A);
        var id = token.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await Collect(collector, id, 0m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Collect(collector, id, -10m)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Collect(collector, id, 900m, (mine, 900m))).StatusCode);       // over the balance
        Assert.Equal(HttpStatusCode.BadRequest, (await Collect(collector, id, 100m, (theirs, 100m))).StatusCode);   // another tenant's charge
        Assert.Equal(HttpStatusCode.BadRequest, (await Collect(collector, id, 100m, (mine, 200m))).StatusCode);     // applies more than collected
        Assert.Equal(0, await PaymentCount(w.Owner, w.TenantA));
        Assert.Equal(HttpStatusCode.OK, (await Resolve(collector, token.GetProperty("code").GetString())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Collect(collector, id, 300m, (mine, 300m))).StatusCode);
    }

    [Fact]
    public async Task A_tenant_can_generate_ten_codes_a_day_not_more()
    {
        var w = await NewWorld("cashlimit");
        for (var i = 0; i < 10; i++) await CashToken(w.A);
        Assert.Equal(HttpStatusCode.Conflict, (await w.A.PostAsync("/api/v1/portal/cash-token", null)).StatusCode);
        await CashToken(w.B); // the limit is per tenant
    }

    [Fact]
    public async Task An_archived_tenants_code_stops_working_and_cannot_be_requested()
    {
        var (owner, _, _) = await fx.RegisterAsync("cashgone");
        var collector = await fx.AddMemberAsync(owner, "Collector");
        var tenantId = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Sin Contrato" }))).GetProperty("id").GetGuid(); // a tenant with a lease cannot be archived
        var tenant = await Login(owner, tenantId);
        var code = (await CashToken(tenant)).GetProperty("code").GetString();
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"/api/v1/tenants/{tenantId}", new { status = "Archived" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Resolve(collector, code)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await tenant.PostAsync("/api/v1/portal/cash-token", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Report(tenant)).StatusCode); // and it cannot report payments either
    }

    [Fact]
    public async Task Cash_collection_is_for_the_roles_that_record_payments()
    {
        var w = await NewWorld("cashroles");
        var code = (await CashToken(w.A)).GetProperty("code").GetString();
        foreach (var role in new[] { "Maintenance", "ReadOnly" })
        {
            var c = await fx.AddMemberAsync(w.Owner, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await Resolve(c, code)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Collect(c, Guid.NewGuid(), 100m)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Resolve(w.A, code)).StatusCode); // a tenant cannot resolve their own (or anyone's) code
        Assert.Equal(HttpStatusCode.Forbidden, (await Resolve(w.B, code)).StatusCode);
        foreach (var role in new[] { "Finance", "Admin" }) Assert.Equal(HttpStatusCode.OK, (await Resolve(await fx.AddMemberAsync(w.Owner, role), code)).StatusCode);
    }

    [Fact]
    public async Task Resolving_codes_is_rate_limited_per_person()
    {
        var w = await NewWorld("cashrate");
        var collectorEmail = $"cobrador-{Guid.NewGuid():N}@example.com";
        var invite = await Json(await w.Owner.PostAsJsonAsync("/api/v1/members/invitations", new { email = collectorEmail, role = "Collector" }));
        var limited = fx.Factory.WithWebHostBuilder(b => { b.UseSetting("RateLimiting:Enabled", "true"); b.UseSetting("RateLimiting:ScansPerMinute", "3"); }).CreateClient();
        Assert.Equal(HttpStatusCode.NoContent,
            (await limited.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token = invite.GetProperty("token").GetString(), fullName = "Cobrador", password = ApiFixture.Password })).StatusCode);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) statuses.Add((await Resolve(limited, "2222-2222-22")).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.BadRequest)); // the first three got through to the code check
    }

    [Fact]
    public async Task A_used_or_revoked_cash_code_can_never_change_or_disappear_in_the_database()
    {
        var w = await NewWorld("cashdb");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector");
        var token = await CashToken(w.A);
        var id = token.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await Collect(collector, id, 100m)).StatusCode);

        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"CashTokens\" SET \"UsedAt\" = NULL, \"PaymentId\" = NULL, \"UsedByUserId\" = NULL WHERE \"Id\" = {0}", id));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"CashTokens\" SET \"ExpiresAt\" = now() + interval '1 year' WHERE \"Id\" = {0}", id));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("DELETE FROM \"CashTokens\" WHERE \"Id\" = {0}", id));
    }

    // ---------- the portal side ----------

    [Fact]
    public async Task The_new_portal_routes_are_for_tenants_only()
    {
        var w = await NewWorld("portalonly");
        var claimId = await ReportOk(w.A);
        foreach (var (method, url) in new[]
        {
            ("GET", "/api/v1/portal/payment-info"), ("GET", "/api/v1/portal/payment-claims"), ("POST", "/api/v1/portal/payment-claims"),
            ("POST", $"/api/v1/portal/payment-claims/{claimId}/cancel"), ("POST", "/api/v1/portal/cash-token"), ("DELETE", "/api/v1/portal/cash-token"),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await w.Owner.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await fx.NewClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) })).StatusCode);
        }
    }

    [Fact]
    public async Task A_tenant_never_sees_another_tenants_reports_or_bank_details_beyond_their_own_unit()
    {
        var w = await NewWorld("portaliso");
        await w.Owner.PostAsJsonAsync("/api/v1/bank-accounts", new { holderName = "Alba Arrendadora", clabe = Clabe(50) });
        await ReportOk(w.A, 111m);
        var raw = await (await w.B.GetAsync("/api/v1/portal/payment-claims")).Content.ReadAsStringAsync();
        Assert.Equal(0, JsonDocument.Parse(raw).RootElement.GetProperty("total").GetInt32());
        var info = await (await w.B.GetAsync("/api/v1/portal/payment-info")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"id\"", info);                 // no internal ids of accounts
        Assert.Contains(Clabe(50), info);
    }
}
