using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Habita.IntegrationTests;

public class Hito4Tests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    // Registered organizations use America/Mexico_City by default.
    static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City")).Date);
    static string D(DateOnly d) => d.ToString("yyyy-MM-dd");

    record World(HttpClient Owner, Guid OrgId, Guid PropertyId, Guid UnitId, Guid TenantId);

    async Task<World> NewWorld(string tag)
    {
        var (owner, orgId, _) = await fx.RegisterAsync(tag);
        var pid = (await Json(await owner.PostAsJsonAsync("/api/v1/properties", new { name = "Torre Norte", address = "Reforma 100" }))).GetProperty("id").GetGuid();
        var uid = (await Json(await owner.PostAsJsonAsync($"/api/v1/properties/{pid}/units", new { code = "101", type = "Apartment", bedrooms = 2, baseRent = 8500 }))).GetProperty("id").GetGuid();
        var tid = (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Ana Ejemplo", email = "ana@example.com" }))).GetProperty("id").GetGuid();
        return new(owner, orgId, pid, uid, tid);
    }

    static Task<HttpResponseMessage> CreateLease(HttpClient c, Guid unitId, Guid tenantId, DateOnly start, DateOnly? end = null, decimal rent = 8500m, int dueDay = 5) =>
        c.PostAsJsonAsync("/api/v1/leases", new { unitId, tenantId, startDate = D(start), endDate = end is null ? null : D(end.Value), monthlyRent = rent, dueDay, depositAmount = 8500 });

    static async Task<Guid> Draft(HttpClient c, Guid unitId, Guid tenantId, DateOnly start, DateOnly? end = null)
    {
        var r = await CreateLease(c, unitId, tenantId, start, end);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    static Task<HttpResponseMessage> Activate(HttpClient c, Guid id) => c.PostAsync($"/api/v1/leases/{id}/activate", null);

    static async Task<string> UnitStatus(HttpClient c, Guid pid, Guid uid) =>
        (await Json(await c.GetAsync($"/api/v1/properties/{pid}/units"))).GetProperty("items").EnumerateArray()
            .First(u => u.GetProperty("id").GetGuid() == uid).GetProperty("status").GetString()!;

    [Fact]
    public async Task Draft_then_activate_and_occupancy_is_derived_from_the_lease_dates()
    {
        var w = await NewWorld("occ");
        var lease = await Draft(w.Owner, w.UnitId, w.TenantId, Today.AddDays(-10), Today.AddDays(355));
        var created = await Json(await w.Owner.GetAsync($"/api/v1/leases/{lease}"));
        Assert.Equal("Draft", created.GetProperty("status").GetString());
        Assert.False(created.GetProperty("isCurrent").GetBoolean());
        Assert.Equal("Torre Norte", created.GetProperty("propertyName").GetString());
        Assert.Equal("Ana Ejemplo", created.GetProperty("tenantName").GetString());
        Assert.Equal("Available", await UnitStatus(w.Owner, w.PropertyId, w.UnitId)); // drafts do not occupy

        Assert.Equal(HttpStatusCode.OK, (await Activate(w.Owner, lease)).StatusCode);
        Assert.Equal("Occupied", await UnitStatus(w.Owner, w.PropertyId, w.UnitId));
        Assert.True((await Json(await w.Owner.GetAsync($"/api/v1/leases/{lease}"))).GetProperty("isCurrent").GetBoolean());

        var occupied = await Json(await w.Owner.GetAsync($"/api/v1/properties/{w.PropertyId}/units?status=Occupied"));
        Assert.Equal(1, occupied.GetProperty("total").GetInt32());
        Assert.Equal(0, (await Json(await w.Owner.GetAsync($"/api/v1/properties/{w.PropertyId}/units?status=Available"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_lease_starting_in_the_future_does_not_occupy_the_unit_yet()
    {
        var w = await NewWorld("future");
        var lease = await Draft(w.Owner, w.UnitId, w.TenantId, Today.AddDays(30));
        Assert.Equal(HttpStatusCode.OK, (await Activate(w.Owner, lease)).StatusCode);
        Assert.Equal("Available", await UnitStatus(w.Owner, w.PropertyId, w.UnitId));
    }

    [Fact]
    public async Task Criterion2_overlapping_active_leases_on_the_same_unit_are_rejected_but_drafts_and_adjacent_ranges_are_fine()
    {
        var w = await NewWorld("overlap");
        var first = await Draft(w.Owner, w.UnitId, w.TenantId, new DateOnly(2030, 1, 1), new DateOnly(2030, 12, 31));
        Assert.Equal(HttpStatusCode.OK, (await Activate(w.Owner, first)).StatusCode);

        var overlapping = await Draft(w.Owner, w.UnitId, w.TenantId, new DateOnly(2030, 12, 31), new DateOnly(2031, 6, 30)); // draft may overlap
        var blocked = await Activate(w.Owner, overlapping);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("solapa", (await Json(blocked)).GetProperty("detail").GetString());

        var openEnded = await Draft(w.Owner, w.UnitId, w.TenantId, new DateOnly(2029, 6, 1)); // no end date overlaps everything after
        Assert.Equal(HttpStatusCode.Conflict, (await Activate(w.Owner, openEnded)).StatusCode);

        var adjacent = await Draft(w.Owner, w.UnitId, w.TenantId, new DateOnly(2031, 1, 1), new DateOnly(2031, 12, 31));
        Assert.Equal(HttpStatusCode.OK, (await Activate(w.Owner, adjacent)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_activation_of_overlapping_leases_lets_exactly_one_win()
    {
        var w = await NewWorld("race4");
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++) ids.Add(await Draft(w.Owner, w.UnitId, w.TenantId, new DateOnly(2032, 1, 1), new DateOnly(2032, 12, 31)));
        var results = await Task.WhenAll(ids.Select(id => Activate(w.Owner, id)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }

    [Fact]
    public async Task Database_itself_rejects_overlapping_leases_in_force_and_invalid_terms()
    {
        var w = await NewWorld("dbc");
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        async Task<PostgresException> Insert(string status, string start, string? end, int dueDay = 5, decimal rent = 100)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO "Leases" ("Id","OrganizationId","UnitId","TenantId","StartDate","EndDate","MonthlyRent","DueDay","Status","CreatedAt","UpdatedAt")
                VALUES (gen_random_uuid(), @o, @u, @t, @s::date, @e::date, @r, @d, @st, now(), now())
                """, conn);
            cmd.Parameters.AddWithValue("o", w.OrgId); cmd.Parameters.AddWithValue("u", w.UnitId); cmd.Parameters.AddWithValue("t", w.TenantId);
            cmd.Parameters.AddWithValue("s", start); cmd.Parameters.AddWithValue("e", (object?)end ?? DBNull.Value);
            cmd.Parameters.AddWithValue("r", rent); cmd.Parameters.AddWithValue("d", dueDay); cmd.Parameters.AddWithValue("st", status);
            return await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        }
        await using (var ok = new NpgsqlCommand("""
            INSERT INTO "Leases" ("Id","OrganizationId","UnitId","TenantId","StartDate","EndDate","MonthlyRent","DueDay","Status","CreatedAt","UpdatedAt")
            VALUES (gen_random_uuid(), @o, @u, @t, '2040-01-01', '2040-12-31', 100, 5, 'Active', now(), now())
            """, conn))
        {
            ok.Parameters.AddWithValue("o", w.OrgId); ok.Parameters.AddWithValue("u", w.UnitId); ok.Parameters.AddWithValue("t", w.TenantId);
            await ok.ExecuteNonQueryAsync();
        }
        Assert.Equal("23P01", (await Insert("Active", "2040-06-01", "2041-01-01")).SqlState);   // exclusion_violation
        Assert.Equal("23P01", (await Insert("Ended", "2039-01-01", null)).SqlState);            // open-ended Ended also reserves
        Assert.Equal("23514", (await Insert("Draft", "2045-01-01", "2044-01-01")).SqlState);    // end before start
        Assert.Equal("23514", (await Insert("Draft", "2045-01-01", null, dueDay: 29)).SqlState);
        Assert.Equal("23514", (await Insert("Draft", "2045-01-01", null, rent: -1)).SqlState);
    }

    [Fact]
    public async Task Validation_rejects_bad_terms_and_missing_references()
    {
        var w = await NewWorld("lval");
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateLease(w.Owner, w.UnitId, w.TenantId, Today, dueDay: 29)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateLease(w.Owner, w.UnitId, w.TenantId, Today, dueDay: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateLease(w.Owner, w.UnitId, w.TenantId, Today, Today.AddDays(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateLease(w.Owner, w.UnitId, w.TenantId, Today, rent: -5)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateLease(w.Owner, Guid.NewGuid(), w.TenantId, Today)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateLease(w.Owner, w.UnitId, Guid.NewGuid(), Today)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateLease(w.Owner, w.UnitId, w.TenantId, Today, Today)).StatusCode); // single-day lease is valid
    }

    [Fact]
    public async Task Criterion9_changing_rent_on_an_active_lease_is_allowed_but_structure_is_locked()
    {
        var w = await NewWorld("edit");
        var id = await Draft(w.Owner, w.UnitId, w.TenantId, Today, Today.AddDays(364));
        var draftEdit = await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{id}", new { startDate = D(Today.AddDays(1)), monthlyRent = 9000 });
        Assert.Equal(HttpStatusCode.OK, draftEdit.StatusCode);

        await Activate(w.Owner, id);
        var rent = await Json(await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{id}", new { monthlyRent = 9500, dueDay = 10, termsNotes = "Incluye agua" }));
        Assert.Equal(9500m, rent.GetProperty("monthlyRent").GetDecimal());
        Assert.Equal(10, rent.GetProperty("dueDay").GetInt32());

        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{id}", new { startDate = D(Today) })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{id}", new { tenantId = w.TenantId })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{id}", new { clearEndDate = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{id}", new { dueDay = 31 })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{id}/terminate", new { endDate = D(Today.AddDays(2)), reason = "Fin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{id}", new { monthlyRent = 1 })).StatusCode);
    }

    [Fact]
    public async Task Terminate_requires_reason_and_valid_date_frees_the_unit_after_the_end_date_and_keeps_history()
    {
        var w = await NewWorld("term");
        var id = await Draft(w.Owner, w.UnitId, w.TenantId, Today.AddDays(-60), Today.AddDays(300));
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{id}/terminate", new { endDate = D(Today), reason = "x" })).StatusCode); // still draft
        await Activate(w.Owner, id);

        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{id}/terminate", new { endDate = D(Today), reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{id}/terminate", new { endDate = D(Today.AddDays(-61)), reason = "x" })).StatusCode); // before start
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{id}/terminate", new { endDate = D(Today.AddDays(301)), reason = "x" })).StatusCode); // cannot extend

        // Terminating in the future keeps the unit occupied until that day.
        var future = await w.Owner.PostAsJsonAsync($"/api/v1/leases/{id}/terminate", new { endDate = D(Today.AddDays(10)), reason = "Aviso de salida" });
        Assert.Equal(HttpStatusCode.OK, future.StatusCode);
        var body = await Json(future);
        Assert.Equal("Ended", body.GetProperty("status").GetString());
        Assert.Equal("Aviso de salida", body.GetProperty("closureReason").GetString());
        Assert.Equal("Occupied", await UnitStatus(w.Owner, w.PropertyId, w.UnitId));
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{id}/terminate", new { endDate = D(Today), reason = "otra vez" })).StatusCode);

        // The reserved range blocks a new lease until the end date, and allows one the day after.
        var tooSoon = await Draft(w.Owner, w.UnitId, w.TenantId, Today.AddDays(5));
        Assert.Equal(HttpStatusCode.Conflict, (await Activate(w.Owner, tooSoon)).StatusCode);
        var next = await Draft(w.Owner, w.UnitId, w.TenantId, Today.AddDays(11));
        Assert.Equal(HttpStatusCode.OK, (await Activate(w.Owner, next)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.GetAsync($"/api/v1/leases/{id}")).StatusCode); // history is kept
    }

    [Fact]
    public async Task Terminating_with_a_past_date_frees_the_unit_immediately()
    {
        var w = await NewWorld("termpast");
        var id = await Draft(w.Owner, w.UnitId, w.TenantId, Today.AddDays(-60));
        await Activate(w.Owner, id);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{id}/terminate", new { endDate = D(Today.AddDays(-1)), reason = "Se fue" })).StatusCode);
        Assert.Equal("Available", await UnitStatus(w.Owner, w.PropertyId, w.UnitId));
    }

    [Fact]
    public async Task Cancel_requires_reason_works_for_draft_and_active_and_releases_the_range()
    {
        var w = await NewWorld("cancel");
        var draft = await Draft(w.Owner, w.UnitId, w.TenantId, Today, Today.AddDays(200));
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{draft}/cancel", new { reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{draft}/cancel", new { reason = "Error de captura" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Activate(w.Owner, draft)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{draft}/cancel", new { reason = "otra vez" })).StatusCode);

        var active = await Draft(w.Owner, w.UnitId, w.TenantId, Today, Today.AddDays(200));
        await Activate(w.Owner, active);
        Assert.Equal("Occupied", await UnitStatus(w.Owner, w.PropertyId, w.UnitId));
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/leases/{active}/cancel", new { reason = "Nunca se mudó" })).StatusCode);
        Assert.Equal("Available", await UnitStatus(w.Owner, w.PropertyId, w.UnitId));
        var replacement = await Draft(w.Owner, w.UnitId, w.TenantId, Today, Today.AddDays(200));
        Assert.Equal(HttpStatusCode.OK, (await Activate(w.Owner, replacement)).StatusCode);
    }

    [Fact]
    public async Task Archiving_a_tenant_unit_or_property_with_an_active_lease_is_blocked_until_it_is_closed()
    {
        var w = await NewWorld("guards");
        var id = await Draft(w.Owner, w.UnitId, w.TenantId, Today);
        await Activate(w.Owner, id);

        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PatchAsJsonAsync($"/api/v1/tenants/{w.TenantId}", new { status = "Archived" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PatchAsJsonAsync($"/api/v1/properties/{w.PropertyId}/units/{w.UnitId}", new { status = "Archived" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PatchAsJsonAsync($"/api/v1/properties/{w.PropertyId}", new { status = "Archived" })).StatusCode);

        await w.Owner.PostAsJsonAsync($"/api/v1/leases/{id}/cancel", new { reason = "Prueba" });
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PatchAsJsonAsync($"/api/v1/tenants/{w.TenantId}", new { status = "Archived" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PatchAsJsonAsync($"/api/v1/properties/{w.PropertyId}", new { status = "Archived" })).StatusCode);

        // And archived entities cannot receive new leases.
        Assert.Equal(HttpStatusCode.Conflict, (await CreateLease(w.Owner, w.UnitId, w.TenantId, Today)).StatusCode);
    }

    [Fact]
    public async Task OrganizationA_cannot_read_or_touch_organizationB_leases_or_reference_its_records()
    {
        var a = await NewWorld("liso-a");
        var b = await NewWorld("liso-b");
        var leaseB = await Draft(b.Owner, b.UnitId, b.TenantId, Today);

        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.GetAsync($"/api/v1/leases/{leaseB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Activate(a.Owner, leaseB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.PatchAsJsonAsync($"/api/v1/leases/{leaseB}", new { monthlyRent = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.PostAsJsonAsync($"/api/v1/leases/{leaseB}/cancel", new { reason = "x" })).StatusCode);
        Assert.Equal(0, (await Json(await a.Owner.GetAsync("/api/v1/leases"))).GetProperty("total").GetInt32());

        // A's lease cannot point at B's unit or tenant (they look nonexistent).
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateLease(a.Owner, b.UnitId, a.TenantId, Today)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateLease(a.Owner, a.UnitId, b.TenantId, Today)).StatusCode);
        Assert.Equal("Draft", (await Json(await b.Owner.GetAsync($"/api/v1/leases/{leaseB}"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Database_rejects_a_lease_that_references_another_organizations_tenant_or_unit()
    {
        var a = await NewWorld("lfk-a");
        var b = await NewWorld("lfk-b");
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "Leases" ("Id","OrganizationId","UnitId","TenantId","StartDate","MonthlyRent","DueDay","Status","CreatedAt","UpdatedAt")
            VALUES (gen_random_uuid(), @o, @u, @t, '2040-01-01', 100, 5, 'Draft', now(), now())
            """, conn);
        cmd.Parameters.AddWithValue("o", a.OrgId); cmd.Parameters.AddWithValue("u", a.UnitId); cmd.Parameters.AddWithValue("t", b.TenantId);
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task Roles_Finance_and_ReadOnly_read_leases_only_Owner_and_Admin_write_and_Maintenance_has_no_lease_access()
    {
        var w = await NewWorld("lroles");
        var id = await Draft(w.Owner, w.UnitId, w.TenantId, Today);
        var admin = await fx.AddMemberAsync(w.Owner, "Admin");
        var finance = await fx.AddMemberAsync(w.Owner, "Finance");
        var readOnly = await fx.AddMemberAsync(w.Owner, "ReadOnly");
        var maintenance = await fx.AddMemberAsync(w.Owner, "Maintenance");

        foreach (var c in new[] { finance, readOnly })
        {
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/leases")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/v1/leases/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await CreateLease(c, w.UnitId, w.TenantId, Today.AddDays(400))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Activate(c, id)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync($"/api/v1/leases/{id}/cancel", new { reason = "x" })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await maintenance.GetAsync("/api/v1/leases")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maintenance.GetAsync($"/api/v1/leases/{id}")).StatusCode);

        // The unit picker works for Maintenance but never exposes rent.
        var picker = await Json(await maintenance.GetAsync("/api/v1/units"));
        Assert.Equal("101", picker.GetProperty("items")[0].GetProperty("code").GetString());
        Assert.False(picker.GetProperty("items")[0].TryGetProperty("baseRent", out _));
        Assert.Equal(8500m, (await Json(await finance.GetAsync("/api/v1/units"))).GetProperty("items")[0].GetProperty("baseRent").GetDecimal());

        Assert.Equal(HttpStatusCode.OK, (await Activate(admin, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.NewClient().GetAsync("/api/v1/leases")).StatusCode);
    }

    [Fact]
    public async Task List_filters_by_status_tenant_and_search()
    {
        var w = await NewWorld("lfilter");
        var luis = (await Json(await w.Owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Luis Prueba" }))).GetProperty("id").GetGuid();
        var u2 = (await Json(await w.Owner.PostAsJsonAsync($"/api/v1/properties/{w.PropertyId}/units", new { code = "202", baseRent = 7000 }))).GetProperty("id").GetGuid();
        var l1 = await Draft(w.Owner, w.UnitId, w.TenantId, Today);
        await Draft(w.Owner, u2, luis, Today);
        await Activate(w.Owner, l1);

        async Task<int> Total(string q) => (await Json(await w.Owner.GetAsync($"/api/v1/leases?{q}"))).GetProperty("total").GetInt32();
        Assert.Equal(2, await Total(""));
        Assert.Equal(1, await Total("status=Active"));
        Assert.Equal(1, await Total("status=Draft"));
        Assert.Equal(1, await Total($"tenantId={luis}"));
        Assert.Equal(1, await Total("search=luis"));
        Assert.Equal(1, await Total("search=202"));
        Assert.Equal(2, await Total("search=torre"));
        Assert.Equal(1, await Total($"unitId={w.UnitId}"));
    }

    [Fact]
    public async Task Lease_changes_are_audited_with_reasons()
    {
        var w = await NewWorld("laud");
        var id = await Draft(w.Owner, w.UnitId, w.TenantId, Today.AddDays(-5));
        await w.Owner.PatchAsJsonAsync($"/api/v1/leases/{id}", new { monthlyRent = 9000 });
        await Activate(w.Owner, id);
        await w.Owner.PostAsJsonAsync($"/api/v1/leases/{id}/terminate", new { endDate = D(Today), reason = "Cambio de ciudad" });

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT "Action", "Reason" FROM "AuditLogs" WHERE "OrganizationId" = @o AND "EntityType" = 'Lease'""", conn);
        cmd.Parameters.AddWithValue("o", w.OrgId);
        var rows = new List<(string, string?)>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) rows.Add((rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1)));
        foreach (var action in new[] { "lease.created", "lease.updated", "lease.activated" }) Assert.Contains(rows, r => r.Item1 == action);
        Assert.Contains(rows, r => r.Item1 == "lease.terminated" && r.Item2 == "Cambio de ciudad");
    }
}
