using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Habita.IntegrationTests;

public class Hito5Tests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    static string D(DateOnly d) => d.ToString("yyyy-MM-dd");
    static DateOnly LocalToday(string tz) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tz)).Date);

    record World(HttpClient Owner, Guid OrgId, Guid PropertyId, Guid UnitId, Guid TenantId);

    async Task<World> NewWorld(string tag)
    {
        var (owner, orgId, _) = await fx.RegisterAsync(tag);
        var pid = (await Json(await owner.PostAsJsonAsync("/api/v1/properties", new { name = "Torre Norte", address = "Reforma 100" }))).GetProperty("id").GetGuid();
        var uid = (await Json(await owner.PostAsJsonAsync($"/api/v1/properties/{pid}/units", new { code = "101", baseRent = 8500 }))).GetProperty("id").GetGuid();
        var tid = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Ana Ejemplo" }))).GetProperty("id").GetGuid();
        return new(owner, orgId, pid, uid, tid);
    }

    static async Task<Guid> Lease(HttpClient c, Guid unitId, Guid tenantId, string start, string? end, decimal rent = 8500m, int dueDay = 5, bool activate = true)
    {
        var r = await c.PostAsJsonAsync("/api/v1/leases", new { unitId, tenantId, startDate = start, endDate = end, monthlyRent = rent, dueDay });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var id = (await Json(r)).GetProperty("id").GetGuid();
        if (activate) Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/v1/leases/{id}/activate", null)).StatusCode);
        return id;
    }

    static Task<HttpResponseMessage> Generate(HttpClient c, int year, int month) => c.PostAsJsonAsync("/api/v1/rent-charges/generate", new { year, month });
    static async Task<JsonElement> Charges(HttpClient c, string query) => await Json(await c.GetAsync($"/api/v1/rent-charges?{query}"));

    static Task<HttpResponseMessage> Manual(HttpClient c, Guid leaseId, string due, decimal amount = 500m, string description = "Cuota de agua", int? year = null, int? month = null) =>
        c.PostAsJsonAsync("/api/v1/rent-charges", new { leaseId, description, amount, dueDate = due, periodYear = year, periodMonth = month });

    [Fact]
    public async Task Criterion3_generation_creates_the_rent_charge_and_running_it_twice_never_duplicates()
    {
        var w = await NewWorld("gen");
        var lease = await Lease(w.Owner, w.UnitId, w.TenantId, "2030-01-01", "2030-12-31", rent: 8500m, dueDay: 5);

        var preview = await Json(await w.Owner.PostAsJsonAsync("/api/v1/rent-charges/generate-preview", new { year = 2030, month = 10 }));
        Assert.Equal(1, preview.GetProperty("toCreate").GetInt32());
        Assert.Equal(8500m, preview.GetProperty("toCreateTotal").GetDecimal());
        Assert.Equal("2030-10-05", preview.GetProperty("items")[0].GetProperty("dueDate").GetString());
        Assert.Equal(0, (await Charges(w.Owner, $"leaseId={lease}")).GetProperty("total").GetInt32()); // preview writes nothing

        var first = await Json(await Generate(w.Owner, 2030, 10));
        Assert.Equal(1, first.GetProperty("created").GetInt32());
        var second = await Json(await Generate(w.Owner, 2030, 10));
        Assert.Equal(0, second.GetProperty("created").GetInt32());
        Assert.Equal(1, second.GetProperty("alreadyExisted").GetInt32());

        var list = await Charges(w.Owner, $"leaseId={lease}");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var c = list.GetProperty("items")[0];
        Assert.Equal("Renta octubre 2030", c.GetProperty("description").GetString());
        Assert.Equal(8500m, c.GetProperty("amount").GetDecimal());
        Assert.Equal(8500m, c.GetProperty("balance").GetDecimal());
        Assert.Equal("Rent", c.GetProperty("type").GetString());
        Assert.Equal("Open", c.GetProperty("status").GetString());
        Assert.Equal("Ana Ejemplo", c.GetProperty("tenantName").GetString());
        Assert.Equal("2030-10-05", c.GetProperty("dueDate").GetString());
    }

    [Fact]
    public async Task Concurrent_generation_runs_create_exactly_one_charge_per_lease()
    {
        var w = await NewWorld("genrace");
        var lease = await Lease(w.Owner, w.UnitId, w.TenantId, "2031-01-01", null);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Generate(w.Owner, 2031, 3)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var created = 0;
        foreach (var r in results) created += (await Json(r)).GetProperty("created").GetInt32();
        Assert.Equal(1, created);
        Assert.Equal(1, (await Charges(w.Owner, $"leaseId={lease}")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Only_leases_covering_the_month_are_charged_in_full_with_no_proration()
    {
        var w = await NewWorld("period");
        var u2 = (await Json(await w.Owner.PostAsJsonAsync($"/api/v1/properties/{w.PropertyId}/units", new { code = "202", baseRent = 1 }))).GetProperty("id").GetGuid();
        var mid = await Lease(w.Owner, w.UnitId, w.TenantId, "2032-10-20", "2032-12-10", rent: 9000m); // enters and leaves mid-month
        var draft = await Lease(w.Owner, u2, w.TenantId, "2032-01-01", null, activate: false);
        var cancelled = await Lease(w.Owner, u2, w.TenantId, "2032-01-01", null, activate: false);
        await w.Owner.PostAsJsonAsync($"/api/v1/leases/{cancelled}/cancel", new { reason = "Error" });

        async Task<int> Created(int y, int m) => (await Json(await Generate(w.Owner, y, m))).GetProperty("created").GetInt32();
        Assert.Equal(0, await Created(2032, 9));   // before the lease starts
        Assert.Equal(1, await Created(2032, 10));  // entry month: full rent
        Assert.Equal(1, await Created(2032, 11));
        Assert.Equal(1, await Created(2032, 12));  // exit month: full rent
        Assert.Equal(0, await Created(2033, 1));   // after the end date

        var rows = (await Charges(w.Owner, $"leaseId={mid}&pageSize=10")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(9000m, r.GetProperty("amount").GetDecimal()));
        Assert.Equal(0, (await Charges(w.Owner, $"leaseId={draft}")).GetProperty("total").GetInt32()); // drafts and cancelled never generate
    }

    [Fact]
    public async Task A_terminated_lease_stops_generating_after_its_end_date_and_zero_rent_is_skipped()
    {
        var w = await NewWorld("term5");
        var lease = await Lease(w.Owner, w.UnitId, w.TenantId, "2033-01-01", null);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{lease}/terminate", new { endDate = "2033-04-15", reason = "Se mudó" })).StatusCode);
        Assert.Equal(1, (await Json(await Generate(w.Owner, 2033, 4))).GetProperty("created").GetInt32());
        Assert.Equal(0, (await Json(await Generate(w.Owner, 2033, 5))).GetProperty("created").GetInt32());

        var u2 = (await Json(await w.Owner.PostAsJsonAsync($"/api/v1/properties/{w.PropertyId}/units", new { code = "303", baseRent = 0 }))).GetProperty("id").GetGuid();
        await Lease(w.Owner, u2, w.TenantId, "2034-01-01", null, rent: 0m);
        var res = await Json(await Generate(w.Owner, 2034, 1));
        Assert.Equal(0, res.GetProperty("created").GetInt32());
        Assert.Equal(1, res.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public async Task Criterion9_a_later_rent_change_does_not_touch_existing_charges()
    {
        var w = await NewWorld("rentchg");
        var lease = await Lease(w.Owner, w.UnitId, w.TenantId, "2035-01-01", null, rent: 8500m);
        await Generate(w.Owner, 2035, 1);
        await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{lease}", new { monthlyRent = 9500 });
        await Generate(w.Owner, 2035, 2);

        var rows = (await Charges(w.Owner, $"leaseId={lease}")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(8500m, rows.First(r => r.GetProperty("periodMonth").GetInt32() == 1).GetProperty("amount").GetDecimal());
        Assert.Equal(9500m, rows.First(r => r.GetProperty("periodMonth").GetInt32() == 2).GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Criterion8_overdue_follows_the_organizations_time_zone_not_the_servers()
    {
        var w = await NewWorld("tz");
        const string tz = "Pacific/Kiritimati"; // UTC+14: its date is often a day ahead of UTC
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""UPDATE "Organizations" SET "TimeZone" = @tz WHERE "Id" = @o""", conn);
            cmd.Parameters.AddWithValue("tz", tz); cmd.Parameters.AddWithValue("o", w.OrgId);
            await cmd.ExecuteNonQueryAsync();
        }
        var lease = await Lease(w.Owner, w.UnitId, w.TenantId, "2036-01-01", null);
        var today = LocalToday(tz);
        foreach (var (offset, tag) in new[] { (-1, "yesterday"), (0, "today"), (1, "tomorrow") })
            Assert.Equal(HttpStatusCode.Created, (await Manual(w.Owner, lease, D(today.AddDays(offset)), description: tag)).StatusCode);

        var items = (await Charges(w.Owner, $"leaseId={lease}")).GetProperty("items").EnumerateArray()
            .ToDictionary(c => c.GetProperty("description").GetString()!, c => c.GetProperty("status").GetString());
        Assert.Equal("Overdue", items["yesterday"]);
        Assert.Equal("Open", items["today"]);      // due today is not overdue yet
        Assert.Equal("Open", items["tomorrow"]);
        Assert.Equal(1, (await Charges(w.Owner, $"leaseId={lease}&status=Overdue")).GetProperty("total").GetInt32());
        Assert.Equal(2, (await Charges(w.Owner, $"leaseId={lease}&status=Open")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Manual_charge_validation_and_lease_state_rules()
    {
        var w = await NewWorld("manual");
        var active = await Lease(w.Owner, w.UnitId, w.TenantId, "2037-01-01", null);
        Assert.Equal(HttpStatusCode.BadRequest, (await Manual(w.Owner, active, "2037-02-01", amount: 0m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Manual(w.Owner, active, "2037-02-01", amount: -5m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Manual(w.Owner, active, "2037-02-01", amount: 10.005m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Manual(w.Owner, active, "2037-02-01", description: " ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Manual(w.Owner, active, "2037-02-01", year: 2037)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Manual(w.Owner, Guid.NewGuid(), "2037-02-01")).StatusCode);

        var u2 = (await Json(await w.Owner.PostAsJsonAsync($"/api/v1/properties/{w.PropertyId}/units", new { code = "9", baseRent = 1 }))).GetProperty("id").GetGuid();
        var draft = await Lease(w.Owner, u2, w.TenantId, "2037-01-01", null, activate: false);
        Assert.Equal(HttpStatusCode.Conflict, (await Manual(w.Owner, draft, "2037-02-01")).StatusCode);

        var ok = await Manual(w.Owner, active, "2037-02-10", amount: 750.50m, description: "Reposición de llave", year: 2037, month: 2);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var body = await Json(ok);
        Assert.Equal("Other", body.GetProperty("type").GetString());
        Assert.Equal(750.50m, body.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Void_needs_a_reason_works_once_only_Owner_or_Admin_and_frees_the_period_for_regeneration()
    {
        var w = await NewWorld("void");
        var lease = await Lease(w.Owner, w.UnitId, w.TenantId, "2038-01-01", null);
        await Generate(w.Owner, 2038, 6);
        var charge = (await Charges(w.Owner, $"leaseId={lease}")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var finance = await fx.AddMemberAsync(w.Owner, "Finance");
        var admin = await fx.AddMemberAsync(w.Owner, "Admin");

        Assert.Equal(HttpStatusCode.Forbidden, (await finance.PostAsJsonAsync($"/api/v1/rent-charges/{charge}/void", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"/api/v1/rent-charges/{charge}/void", new { reason = " " })).StatusCode);

        var voided = await Json(await admin.PostAsJsonAsync($"/api/v1/rent-charges/{charge}/void", new { reason = "Renta mal capturada" }));
        Assert.Equal("Voided", voided.GetProperty("status").GetString());
        Assert.Equal(0m, voided.GetProperty("balance").GetDecimal());
        Assert.Equal("Renta mal capturada", voided.GetProperty("voidReason").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"/api/v1/rent-charges/{charge}/void", new { reason = "otra vez" })).StatusCode);
        Assert.Equal(1, (await Charges(w.Owner, $"leaseId={lease}&status=Voided")).GetProperty("total").GetInt32());

        // Voiding frees the slot: the period can be regenerated (e.g. after fixing the rent) without touching the voided record.
        await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{lease}", new { monthlyRent = 9100 });
        var preview = await Json(await w.Owner.PostAsJsonAsync("/api/v1/rent-charges/generate-preview", new { year = 2038, month = 6 }));
        Assert.Equal(1, preview.GetProperty("toCreate").GetInt32());
        Assert.Equal(1, (await Json(await Generate(w.Owner, 2038, 6))).GetProperty("created").GetInt32());
        Assert.Equal(2, (await Charges(w.Owner, $"leaseId={lease}")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Roles_Finance_creates_and_generates_ReadOnly_reads_Maintenance_has_no_access()
    {
        var w = await NewWorld("roles5");
        var lease = await Lease(w.Owner, w.UnitId, w.TenantId, "2039-01-01", null);
        var finance = await fx.AddMemberAsync(w.Owner, "Finance");
        var readOnly = await fx.AddMemberAsync(w.Owner, "ReadOnly");
        var maintenance = await fx.AddMemberAsync(w.Owner, "Maintenance");

        Assert.Equal(HttpStatusCode.OK, (await finance.PostAsJsonAsync("/api/v1/rent-charges/generate-preview", new { year = 2039, month = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Generate(finance, 2039, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Manual(finance, lease, "2039-02-01")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await finance.GetAsync("/api/v1/rent-charges")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync("/api/v1/rent-charges")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Generate(readOnly, 2039, 2)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Manual(readOnly, lease, "2039-02-01")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync("/api/v1/rent-charges/generate-preview", new { year = 2039, month = 2 })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await maintenance.GetAsync("/api/v1/rent-charges")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Generate(maintenance, 2039, 2)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.NewClient().GetAsync("/api/v1/rent-charges")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Generate(w.Owner, 1999, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Generate(w.Owner, 2039, 13)).StatusCode);
    }

    [Fact]
    public async Task OrganizationA_cannot_read_void_or_generate_into_organizationB()
    {
        var a = await NewWorld("iso5-a");
        var b = await NewWorld("iso5-b");
        var leaseA = await Lease(a.Owner, a.UnitId, a.TenantId, "2040-01-01", null);
        var leaseB = await Lease(b.Owner, b.UnitId, b.TenantId, "2040-01-01", null);
        await Generate(a.Owner, 2040, 1);
        var chargeA = (await Charges(a.Owner, $"leaseId={leaseA}")).GetProperty("items")[0].GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/rent-charges/{chargeA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PostAsJsonAsync($"/api/v1/rent-charges/{chargeA}/void", new { reason = "x" })).StatusCode);
        Assert.Equal(0, (await Charges(b.Owner, "")).GetProperty("total").GetInt32()); // B has no charges yet
        Assert.Equal(HttpStatusCode.BadRequest, (await Manual(b.Owner, leaseA, "2040-02-01")).StatusCode); // A's lease looks nonexistent to B

        // B generating only ever touches B's lease.
        Assert.Equal(1, (await Json(await Generate(b.Owner, 2040, 1))).GetProperty("created").GetInt32());
        Assert.Equal(1, (await Charges(a.Owner, "")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Charges(b.Owner, $"leaseId={leaseB}")).GetProperty("total").GetInt32());
        Assert.Equal("Open", (await Json(await a.Owner.GetAsync($"/api/v1/rent-charges/{chargeA}"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Database_enforces_one_live_rent_charge_per_period_valid_statuses_positive_amounts_and_same_organization_leases()
    {
        var a = await NewWorld("dbc5-a");
        var b = await NewWorld("dbc5-b");
        var leaseA = await Lease(a.Owner, a.UnitId, a.TenantId, "2041-01-01", null);
        var leaseB = await Lease(b.Owner, b.UnitId, b.TenantId, "2041-01-01", null);

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        async Task<PostgresException> Insert(Guid org, Guid lease, string type, int? year, int? month, string status = "Open", decimal amount = 100m)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO "RentCharges" ("Id","OrganizationId","LeaseId","Type","PeriodYear","PeriodMonth","Description","Amount","DueDate","Status","CreatedAt","UpdatedAt")
                VALUES (gen_random_uuid(), @o, @l, @t, @y, @m, 'x', @a, '2041-02-05', @s, now(), now())
                """, conn);
            cmd.Parameters.AddWithValue("o", org); cmd.Parameters.AddWithValue("l", lease); cmd.Parameters.AddWithValue("t", type);
            cmd.Parameters.AddWithValue("y", (object?)year ?? DBNull.Value); cmd.Parameters.AddWithValue("m", (object?)month ?? DBNull.Value);
            cmd.Parameters.AddWithValue("a", amount); cmd.Parameters.AddWithValue("s", status);
            return await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        }

        await Generate(a.Owner, 2041, 2);
        Assert.Equal("23505", (await Insert(a.OrgId, leaseA, "Rent", 2041, 2)).SqlState);                 // unique_violation: same lease+period
        Assert.Equal("23514", (await Insert(a.OrgId, leaseA, "Rent", 2041, 3, status: "Overdue")).SqlState); // Overdue is never stored
        Assert.Equal("23514", (await Insert(a.OrgId, leaseA, "Rent", 2041, 3, amount: 0m)).SqlState);
        Assert.Equal("23514", (await Insert(a.OrgId, leaseA, "Rent", null, null)).SqlState);               // rent needs a period
        Assert.Equal("23514", (await Insert(a.OrgId, leaseA, "Other", 2041, null)).SqlState);              // period is both or neither
        Assert.Equal("23503", (await Insert(a.OrgId, leaseB, "Other", null, null)).SqlState);              // composite FK: B's lease under A
    }

    [Fact]
    public async Task Filters_and_search_work_and_charges_are_audited()
    {
        var w = await NewWorld("filt5");
        var lease = await Lease(w.Owner, w.UnitId, w.TenantId, "2042-01-01", null);
        await Generate(w.Owner, 2042, 1);
        await Generate(w.Owner, 2042, 2);
        var extra = await Json(await Manual(w.Owner, lease, "2042-03-01", description: "Multa por ruido"));
        await w.Owner.PostAsJsonAsync($"/api/v1/rent-charges/{extra.GetProperty("id").GetGuid()}/void", new { reason = "Improcedente" });

        Assert.Equal(3, (await Charges(w.Owner, $"leaseId={lease}")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Charges(w.Owner, $"leaseId={lease}&month=2&year=2042")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Charges(w.Owner, $"leaseId={lease}&search=multa")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Charges(w.Owner, $"tenantId={w.TenantId}&status=Overdue")).GetProperty("total").GetInt32()); // 2042 charges are not yet due
        Assert.Equal(1, (await Charges(w.Owner, $"leaseId={lease}&pageSize=1&page=3")).GetProperty("items").GetArrayLength());

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT "Action", "Reason" FROM "AuditLogs" WHERE "OrganizationId" = @o AND "EntityType" IN ('RentCharge','ChargeBatch')""", conn);
        cmd.Parameters.AddWithValue("o", w.OrgId);
        var rows = new List<(string, string?)>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) rows.Add((rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1)));
        Assert.Equal(2, rows.Count(r => r.Item1 == "charges.generated"));
        Assert.Contains(rows, r => r.Item1 == "charge.created");
        Assert.Contains(rows, r => r.Item1 == "charge.voided" && r.Item2 == "Improcedente");
    }
}
