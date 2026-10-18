using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Habita.IntegrationTests;

public class Hito7Tests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    record World(HttpClient Owner, Guid OrgId, Guid PropertyId, Guid UnitId);

    async Task<World> NewWorld(string tag)
    {
        var (owner, orgId, _) = await fx.RegisterAsync(tag);
        var pid = (await Json(await owner.PostAsJsonAsync("/api/v1/properties", new { name = "Torre Norte", address = "Reforma 100" }))).GetProperty("id").GetGuid();
        var uid = (await Json(await owner.PostAsJsonAsync($"/api/v1/properties/{pid}/units", new { code = "101", baseRent = 8500 }))).GetProperty("id").GetGuid();
        return new(owner, orgId, pid, uid);
    }

    static async Task<(Guid MemberId, Guid UserId)> Member(HttpClient owner, string role)
    {
        var m = (await Json(await owner.GetAsync("/api/v1/members"))).EnumerateArray().First(x => x.GetProperty("role").GetString() == role);
        return (m.GetProperty("id").GetGuid(), m.GetProperty("userId").GetGuid());
    }

    static async Task<Guid> Issue(HttpClient c, Guid propertyId, Guid? unitId = null, string title = "Fuga en lavabo", string priority = "Normal", Guid? assignTo = null)
    {
        var r = await c.PostAsJsonAsync("/api/v1/maintenance-issues", new { propertyId, unitId, title, description = "Gotea desde ayer", priority, assignedToUserId = assignTo });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    static Task<HttpResponseMessage> SetStatus(HttpClient c, Guid id, string status, string? note = null) => c.PostAsJsonAsync($"/api/v1/maintenance-issues/{id}/status", new { status, note });
    static Task<HttpResponseMessage> Comment(HttpClient c, Guid id, string body) => c.PostAsJsonAsync($"/api/v1/maintenance-issues/{id}/comments", new { body });
    static Task<HttpResponseMessage> Assign(HttpClient c, Guid id, Guid? userId) => c.PostAsJsonAsync($"/api/v1/maintenance-issues/{id}/assign", new { userId });
    static async Task<JsonElement> Get(HttpClient c, Guid id) => await Json(await c.GetAsync($"/api/v1/maintenance-issues/{id}"));

    [Fact]
    public async Task Owner_creates_an_issue_with_defaults_and_a_creation_event()
    {
        var w = await NewWorld("mcreate");
        var id = await Issue(w.Owner, w.PropertyId, w.UnitId);
        var i = await Get(w.Owner, id);
        Assert.Equal("Fuga en lavabo", i.GetProperty("title").GetString());
        Assert.Equal("Normal", i.GetProperty("priority").GetString());
        Assert.Equal("Open", i.GetProperty("status").GetString());
        Assert.Equal("Torre Norte", i.GetProperty("propertyName").GetString());
        Assert.Equal("101", i.GetProperty("unitCode").GetString());
        Assert.Equal(JsonValueKind.Null, i.GetProperty("resolvedAt").ValueKind);
        var events = i.GetProperty("events");
        Assert.Equal(1, events.GetArrayLength());
        Assert.Equal("Created", events[0].GetProperty("type").GetString());
        Assert.Equal("Usuario Prueba", events[0].GetProperty("actorName").GetString());

        // Property-level issue without a unit is valid.
        Assert.Equal(HttpStatusCode.Created, (await w.Owner.PostAsJsonAsync("/api/v1/maintenance-issues", new { propertyId = w.PropertyId, title = "Portón dañado" })).StatusCode);
    }

    [Fact]
    public async Task Validation_rejects_bad_issues()
    {
        var w = await NewWorld("mval");
        var other = (await Json(await w.Owner.PostAsJsonAsync("/api/v1/properties", new { name = "Otra", address = "x" }))).GetProperty("id").GetGuid();
        var foreignUnit = (await Json(await w.Owner.PostAsJsonAsync($"/api/v1/properties/{other}/units", new { code = "9", baseRent = 1 }))).GetProperty("id").GetGuid();

        Task<HttpResponseMessage> Post(object body) => w.Owner.PostAsJsonAsync("/api/v1/maintenance-issues", body);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { propertyId = w.PropertyId, title = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { propertyId = w.PropertyId, title = new string('t', 201) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { propertyId = w.PropertyId, title = "x", description = new string('d', 4001) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { propertyId = w.PropertyId, title = "x", priority = "Critical" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { propertyId = Guid.NewGuid(), title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { propertyId = w.PropertyId, unitId = foreignUnit, title = "x" })).StatusCode); // unit of another property
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { propertyId = w.PropertyId, unitId = Guid.NewGuid(), title = "x" })).StatusCode);

        await w.Owner.PatchAsJsonAsync($"/api/v1/properties/{w.PropertyId}/units/{w.UnitId}", new { status = "Archived" });
        Assert.Equal(HttpStatusCode.Conflict, (await Post(new { propertyId = w.PropertyId, unitId = w.UnitId, title = "x" })).StatusCode);
        await w.Owner.PatchAsJsonAsync($"/api/v1/properties/{w.PropertyId}", new { status = "Archived" });
        Assert.Equal(HttpStatusCode.Conflict, (await Post(new { propertyId = w.PropertyId, title = "x" })).StatusCode);
    }

    [Fact]
    public async Task Assignment_only_to_active_maintenance_admin_or_owner_members_of_this_organization()
    {
        var w = await NewWorld("massign");
        var id = await Issue(w.Owner, w.PropertyId);
        await fx.AddMemberAsync(w.Owner, "Maintenance");
        await fx.AddMemberAsync(w.Owner, "Finance");
        await fx.AddMemberAsync(w.Owner, "ReadOnly");
        await fx.AddMemberAsync(w.Owner, "Admin");
        var maint = await Member(w.Owner, "Maintenance");
        var finance = await Member(w.Owner, "Finance");
        var readOnly = await Member(w.Owner, "ReadOnly");
        var admin = await Member(w.Owner, "Admin");
        var owner = await Member(w.Owner, "Owner");

        Assert.Equal(HttpStatusCode.BadRequest, (await Assign(w.Owner, id, finance.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Assign(w.Owner, id, readOnly.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Assign(w.Owner, id, Guid.NewGuid())).StatusCode);
        var (outsiderClient, _, _) = await fx.RegisterAsync("outsider");
        var outsiderUser = (await Json(await outsiderClient.GetAsync("/api/v1/me"))).GetProperty("user").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await Assign(w.Owner, id, outsiderUser)).StatusCode); // exists, but not a member of this organization

        var ok = await Assign(w.Owner, id, maint.UserId);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(maint.UserId, (await Json(ok)).GetProperty("assignedToUserId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await Assign(w.Owner, id, admin.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Assign(w.Owner, id, owner.UserId)).StatusCode);

        var before = (await Get(w.Owner, id)).GetProperty("events").GetArrayLength();
        Assert.Equal(HttpStatusCode.OK, (await Assign(w.Owner, id, owner.UserId)).StatusCode); // same assignee: no new event
        Assert.Equal(before, (await Get(w.Owner, id)).GetProperty("events").GetArrayLength());

        var unassigned = await Json(await Assign(w.Owner, id, null));
        Assert.Equal(JsonValueKind.Null, unassigned.GetProperty("assignedToUserId").ValueKind);

        // A deactivated member can no longer be assigned.
        await w.Owner.PatchAsJsonAsync($"/api/v1/members/{maint.MemberId}", new { status = "Deactivated" });
        Assert.Equal(HttpStatusCode.BadRequest, (await Assign(w.Owner, id, maint.UserId)).StatusCode);
    }

    [Fact]
    public async Task Status_transitions_follow_the_graph_and_resolving_records_who_and_when()
    {
        var w = await NewWorld("mflow");
        var admin = await fx.AddMemberAsync(w.Owner, "Admin");
        var id = await Issue(w.Owner, w.PropertyId);

        Assert.Equal(HttpStatusCode.Conflict, (await SetStatus(w.Owner, id, "Closed")).StatusCode);   // must resolve first
        Assert.Equal(HttpStatusCode.Conflict, (await SetStatus(w.Owner, id, "Open")).StatusCode);     // same status
        Assert.Equal(HttpStatusCode.BadRequest, (await SetStatus(w.Owner, id, "Done")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetStatus(w.Owner, id, "InProgress", "Se pidió el repuesto")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetStatus(w.Owner, id, "Waiting")).StatusCode);

        var resolved = await Json(await SetStatus(admin, id, "Resolved", "Cambiado el sello"));
        Assert.Equal("Resolved", resolved.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, resolved.GetProperty("resolvedAt").ValueKind);
        Assert.Equal("Admin", resolved.GetProperty("resolvedByName").GetString());

        var reopened = await Json(await SetStatus(w.Owner, id, "InProgress", "Sigue goteando"));
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("resolvedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("resolvedByName").ValueKind);

        await SetStatus(w.Owner, id, "Resolved");
        var closed = await Json(await SetStatus(w.Owner, id, "Closed"));
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, closed.GetProperty("resolvedAt").ValueKind); // closing keeps the resolution data

        Assert.Equal(HttpStatusCode.Conflict, (await SetStatus(w.Owner, id, "InProgress")).StatusCode); // closed is final
        Assert.Equal(HttpStatusCode.Conflict, (await Comment(w.Owner, id, "tarde")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PatchAsJsonAsync($"/api/v1/maintenance-issues/{id}", new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Assign(w.Owner, id, null)).StatusCode);

        // The full history is kept, in order, with who did each change.
        var events = (await Get(w.Owner, id)).GetProperty("events").EnumerateArray().ToList();
        var changes = events.Where(e => e.GetProperty("type").GetString() == "StatusChanged").Select(e => $"{e.GetProperty("fromStatus").GetString()}>{e.GetProperty("toStatus").GetString()}").ToList();
        Assert.Equal(["Open>InProgress", "InProgress>Waiting", "Waiting>Resolved", "Resolved>InProgress", "InProgress>Resolved", "Resolved>Closed"], changes);
        Assert.Equal("Se pidió el repuesto", events.First(e => e.GetProperty("toStatus").GetString() == "InProgress").GetProperty("body").GetString());
    }

    [Fact]
    public async Task Maintenance_staff_see_every_issue_but_act_only_on_their_own_and_never_close()
    {
        var w = await NewWorld("mperm");
        var m1 = await fx.AddMemberAsync(w.Owner, "Maintenance");
        var m1Id = (await Member(w.Owner, "Maintenance")).UserId;
        var mine = await Issue(w.Owner, w.PropertyId, title: "Mía", assignTo: m1Id);
        var notMine = await Issue(w.Owner, w.PropertyId, title: "Ajena");

        Assert.Equal(2, (await Json(await m1.GetAsync("/api/v1/maintenance-issues"))).GetProperty("total").GetInt32()); // sees all (operational data)
        Assert.Equal(HttpStatusCode.OK, (await m1.GetAsync($"/api/v1/maintenance-issues/{notMine}")).StatusCode);
        Assert.Equal(1, (await Json(await m1.GetAsync("/api/v1/maintenance-issues?assignedToMe=true"))).GetProperty("total").GetInt32());

        // Not assigned to them: no changes, no comments.
        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatus(m1, notMine, "InProgress")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Comment(m1, notMine, "x")).StatusCode);

        // Assigned to them: comment and move forward, but not close or reset to open.
        Assert.Equal(HttpStatusCode.OK, (await Comment(m1, mine, "Voy mañana a primera hora")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetStatus(m1, mine, "InProgress")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetStatus(m1, mine, "Waiting", "Falta refacción")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatus(m1, mine, "Open")).StatusCode);
        var resolved = await SetStatus(m1, mine, "Resolved", "Listo");
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatus(m1, mine, "Closed")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetStatus(m1, mine, "InProgress", "Reabierta")).StatusCode); // reopen their own

        // They cannot create, edit, or assign.
        Assert.Equal(HttpStatusCode.Forbidden, (await m1.PostAsJsonAsync("/api/v1/maintenance-issues", new { propertyId = w.PropertyId, title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await m1.PatchAsJsonAsync($"/api/v1/maintenance-issues/{mine}", new { priority = "Urgent" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Assign(m1, mine, m1Id)).StatusCode);

        // Another maintenance user cannot touch it either.
        var m2 = await fx.AddMemberAsync(w.Owner, "Maintenance");
        Assert.Equal(HttpStatusCode.Forbidden, (await Comment(m2, mine, "intruso")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatus(m2, mine, "Resolved")).StatusCode);
    }

    [Fact]
    public async Task Maintenance_never_receives_financial_or_tenant_data()
    {
        var w = await NewWorld("mfin");
        var tenant = (await Json(await w.Owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Ana Ejemplo", email = "ana@example.com" }))).GetProperty("id").GetGuid();
        var lease = (await Json(await w.Owner.PostAsJsonAsync("/api/v1/leases", new { unitId = w.UnitId, tenantId = tenant, startDate = "2030-01-01", monthlyRent = 8500, dueDay = 5 }))).GetProperty("id").GetGuid();
        await w.Owner.PostAsync($"/api/v1/leases/{lease}/activate", null);

        var m = await fx.AddMemberAsync(w.Owner, "Maintenance");
        var id = await Issue(w.Owner, w.PropertyId, w.UnitId);

        foreach (var url in new[] { "/api/v1/leases", "/api/v1/rent-charges", "/api/v1/payments", "/api/v1/tenants", $"/api/v1/leases/{lease}", $"/api/v1/tenants/{tenant}" })
            Assert.Equal(HttpStatusCode.Forbidden, (await m.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await m.PostAsJsonAsync("/api/v1/rent-charges/generate-preview", new { year = 2030, month = 1 })).StatusCode);

        var raw = await (await m.GetAsync($"/api/v1/maintenance-issues/{id}")).Content.ReadAsStringAsync();
        var list = await (await m.GetAsync("/api/v1/maintenance-issues")).Content.ReadAsStringAsync();
        foreach (var forbidden in new[] { "rent", "Rent", "amount", "Amount", "payment", "tenant", "Tenant", "ana@example.com", "8500" })
        {
            Assert.DoesNotContain(forbidden, raw);
            Assert.DoesNotContain(forbidden, list);
        }
        var units = await Json(await m.GetAsync($"/api/v1/properties/{w.PropertyId}/units"));
        Assert.False(units.GetProperty("items")[0].TryGetProperty("baseRent", out _));
    }

    [Fact]
    public async Task Comments_are_validated_ordered_and_only_writers_can_add_them()
    {
        var w = await NewWorld("mcomm");
        var id = await Issue(w.Owner, w.PropertyId);
        var finance = await fx.AddMemberAsync(w.Owner, "Finance");
        var readOnly = await fx.AddMemberAsync(w.Owner, "ReadOnly");

        Assert.Equal(HttpStatusCode.BadRequest, (await Comment(w.Owner, id, "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Comment(w.Owner, id, new string('c', 2001))).StatusCode);
        await Comment(w.Owner, id, "Primero");
        await Comment(w.Owner, id, "Segundo");
        var comments = (await Get(w.Owner, id)).GetProperty("events").EnumerateArray().Where(e => e.GetProperty("type").GetString() == "Comment").Select(e => e.GetProperty("body").GetString()).ToList();
        Assert.Equal(["Primero", "Segundo"], comments);

        foreach (var c in new[] { finance, readOnly })
        {
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/v1/maintenance-issues/{id}")).StatusCode); // can read
            Assert.Equal(HttpStatusCode.Forbidden, (await Comment(c, id, "no")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SetStatus(c, id, "InProgress")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.NewClient().GetAsync("/api/v1/maintenance-issues")).StatusCode);
    }

    [Fact]
    public async Task Owner_edits_fields_and_every_change_lands_in_the_history()
    {
        var w = await NewWorld("medit");
        var id = await Issue(w.Owner, w.PropertyId, w.UnitId, priority: "Low");
        var upd = await Json(await w.Owner.PatchAsJsonAsync($"/api/v1/maintenance-issues/{id}", new { title = "Fuga grave", priority = "Urgent", clearUnit = true }));
        Assert.Equal("Fuga grave", upd.GetProperty("title").GetString());
        Assert.Equal("Urgent", upd.GetProperty("priority").GetString());
        Assert.Equal(JsonValueKind.Null, upd.GetProperty("unitId").ValueKind);
        var ev = upd.GetProperty("events").EnumerateArray().Last();
        Assert.Equal("Updated", ev.GetProperty("type").GetString());
        Assert.Contains("prioridad Low → Urgent", ev.GetProperty("body").GetString());

        var count = upd.GetProperty("events").GetArrayLength();
        var noop = await Json(await w.Owner.PatchAsJsonAsync($"/api/v1/maintenance-issues/{id}", new { title = "Fuga grave" }));
        Assert.Equal(count, noop.GetProperty("events").GetArrayLength()); // nothing changed, nothing recorded
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PatchAsJsonAsync($"/api/v1/maintenance-issues/{id}", new { title = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PatchAsJsonAsync($"/api/v1/maintenance-issues/{id}", new { unitId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PatchAsJsonAsync($"/api/v1/maintenance-issues/{id}", new { unitId = w.UnitId })).StatusCode);
    }

    [Fact]
    public async Task List_is_ordered_by_priority_and_supports_all_filters_and_pagination()
    {
        var w = await NewWorld("mlist");
        var m = await fx.AddMemberAsync(w.Owner, "Maintenance");
        var mId = (await Member(w.Owner, "Maintenance")).UserId;
        var low = await Issue(w.Owner, w.PropertyId, title: "Pintura", priority: "Low");
        var urgent = await Issue(w.Owner, w.PropertyId, w.UnitId, title: "Fuga de gas", priority: "Urgent", assignTo: mId);
        var high = await Issue(w.Owner, w.PropertyId, title: "Puerta rota", priority: "High");
        var normal = await Issue(w.Owner, w.PropertyId, title: "Foco fundido", priority: "Normal");
        var otherProp = (await Json(await w.Owner.PostAsJsonAsync("/api/v1/properties", new { name = "Casa Jardín", address = "x" }))).GetProperty("id").GetGuid();
        var elsewhere = await Issue(w.Owner, otherProp, title: "Techo", priority: "Normal");
        await SetStatus(w.Owner, low, "Resolved");
        await SetStatus(w.Owner, low, "Closed");

        async Task<List<string>> Titles(string q) => (await Json(await w.Owner.GetAsync($"/api/v1/maintenance-issues?{q}"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).ToList();
        var all = await Titles("");
        Assert.Equal(["Fuga de gas", "Puerta rota"], all.Take(2).ToList()); // Urgent, then High
        Assert.Equal("Pintura", all.Last());                                 // Low last
        Assert.Equal(5, all.Count);

        Assert.Equal(4, (await Titles("activeOnly=true")).Count); // excludes the closed one
        Assert.Equal(["Pintura"], await Titles("status=Closed"));
        Assert.Equal(["Fuga de gas"], await Titles("priority=Urgent"));
        Assert.Equal(["Techo"], await Titles($"propertyId={otherProp}"));
        Assert.Equal(["Fuga de gas"], await Titles($"unitId={w.UnitId}"));
        Assert.Equal(["Fuga de gas"], await Titles($"assignedToUserId={mId}"));
        Assert.Equal(["Puerta rota"], await Titles("search=puerta"));
        Assert.Equal(["Techo"], await Titles("search=jard"));  // property name
        Assert.Equal(["Fuga de gas"], await Titles("search=101")); // unit code
        Assert.Equal(["Fuga de gas"], (await Json(await m.GetAsync("/api/v1/maintenance-issues?assignedToMe=true"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).ToList());
        var page2 = await Json(await w.Owner.GetAsync("/api/v1/maintenance-issues?pageSize=2&page=2"));
        Assert.Equal(2, page2.GetProperty("items").GetArrayLength());
        Assert.Equal(5, page2.GetProperty("total").GetInt32());
        _ = (normal, high, elsewhere);
    }

    [Fact]
    public async Task OrganizationA_cannot_read_modify_or_reach_into_organizationB()
    {
        var a = await NewWorld("miso-a");
        var b = await NewWorld("miso-b");
        var issueA = await Issue(a.Owner, a.PropertyId, a.UnitId);
        var issueB = await Issue(b.Owner, b.PropertyId, b.UnitId);
        var aMaint = await fx.AddMemberAsync(a.Owner, "Maintenance");
        var aMaintUser = (await Member(a.Owner, "Maintenance")).UserId;

        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/maintenance-issues/{issueA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetStatus(b.Owner, issueA, "InProgress")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Comment(b.Owner, issueA, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Assign(b.Owner, issueA, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PatchAsJsonAsync($"/api/v1/maintenance-issues/{issueA}", new { title = "x" })).StatusCode);
        Assert.Equal(1, (await Json(await b.Owner.GetAsync("/api/v1/maintenance-issues"))).GetProperty("total").GetInt32()); // only B's own

        Assert.Equal(HttpStatusCode.BadRequest, (await b.Owner.PostAsJsonAsync("/api/v1/maintenance-issues", new { propertyId = a.PropertyId, title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Owner.PostAsJsonAsync("/api/v1/maintenance-issues", new { propertyId = b.PropertyId, unitId = a.UnitId, title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Assign(b.Owner, issueB, aMaintUser)).StatusCode); // A's user is not B's member
        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatus(aMaint, issueA, "Resolved")).StatusCode);   // A's maintenance, unassigned

        Assert.Equal("Open", (await Get(a.Owner, issueA)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Database_enforces_unit_belongs_to_property_resolution_data_and_append_only_history()
    {
        var w = await NewWorld("mdb");
        var other = (await Json(await w.Owner.PostAsJsonAsync("/api/v1/properties", new { name = "Otra", address = "x" }))).GetProperty("id").GetGuid();
        var foreignUnit = (await Json(await w.Owner.PostAsJsonAsync($"/api/v1/properties/{other}/units", new { code = "9", baseRent = 1 }))).GetProperty("id").GetGuid();
        var issue = await Issue(w.Owner, w.PropertyId, w.UnitId);
        var creator = (await Member(w.Owner, "Owner")).UserId;

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        async Task<string> Fails(string sql, params (string, object)[] p)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v);
            return (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState!;
        }
        // Unit of another property under this property: composite foreign key.
        Assert.Equal("23503", await Fails("""
            INSERT INTO "MaintenanceIssues" ("Id","OrganizationId","PropertyId","UnitId","Title","Description","Priority","Status","ReportedAt","CreatedByUserId","UpdatedAt")
            VALUES (gen_random_uuid(), @o, @p, @u, 't', '', 'Normal', 'Open', now(), @c, now())
            """, ("o", w.OrgId), ("p", w.PropertyId), ("u", foreignUnit), ("c", creator)));
        // Resolved without who/when.
        Assert.Equal("23514", await Fails("""UPDATE "MaintenanceIssues" SET "Status" = 'Resolved' WHERE "Id" = @i""", ("i", issue)));
        // An unresolved issue cannot carry resolution data.
        Assert.Equal("23514", await Fails("""UPDATE "MaintenanceIssues" SET "ResolvedAt" = now(), "ResolvedByUserId" = @c WHERE "Id" = @i""", ("i", issue), ("c", creator)));
        Assert.Equal("23514", await Fails("""UPDATE "MaintenanceIssues" SET "Priority" = 'Critical' WHERE "Id" = @i""", ("i", issue)));
        // History is append-only.
        Assert.Equal("23514", await Fails("""UPDATE "MaintenanceIssueEvents" SET "Body" = 'borrado' WHERE "IssueId" = @i""", ("i", issue)));
        Assert.Equal("23514", await Fails("""DELETE FROM "MaintenanceIssueEvents" WHERE "IssueId" = @i""", ("i", issue)));
    }

    [Fact]
    public async Task Concurrent_status_changes_never_overwrite_each_other_or_fail_with_server_errors()
    {
        var w = await NewWorld("mrace");
        var id = await Issue(w.Owner, w.PropertyId);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SetStatus(w.Owner, id, "Resolved", "ok")));
        Assert.DoesNotContain(results, r => (int)r.StatusCode >= 500);
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var events = (await Get(w.Owner, id)).GetProperty("events").EnumerateArray().Where(e => e.GetProperty("type").GetString() == "StatusChanged").ToList();
        Assert.Single(events); // exactly one transition was recorded

        // A mix of different operations on the same issue also ends consistent and error-free.
        var id2 = await Issue(w.Owner, w.PropertyId);
        var mix = await Task.WhenAll(
            SetStatus(w.Owner, id2, "InProgress"), SetStatus(w.Owner, id2, "Waiting"), SetStatus(w.Owner, id2, "Resolved"),
            Comment(w.Owner, id2, "a"), Comment(w.Owner, id2, "b"), w.Owner.PatchAsJsonAsync($"/api/v1/maintenance-issues/{id2}", new { priority = "High" }));
        Assert.DoesNotContain(mix, r => (int)r.StatusCode >= 500);
        var final = await Get(w.Owner, id2);
        Assert.Equal(2, final.GetProperty("events").EnumerateArray().Count(e => e.GetProperty("type").GetString() == "Comment"));
    }

    [Fact]
    public async Task Creation_assignment_and_resolution_are_audited()
    {
        var w = await NewWorld("maud");
        await fx.AddMemberAsync(w.Owner, "Maintenance");
        var mId = (await Member(w.Owner, "Maintenance")).UserId;
        var id = await Issue(w.Owner, w.PropertyId, assignTo: mId);
        await SetStatus(w.Owner, id, "Resolved");
        await w.Owner.PatchAsJsonAsync($"/api/v1/maintenance-issues/{id}", new { priority = "High" });

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT "Action" FROM "AuditLogs" WHERE "OrganizationId" = @o AND "EntityType" = 'MaintenanceIssue'""", conn);
        cmd.Parameters.AddWithValue("o", w.OrgId);
        var actions = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) actions.Add(rd.GetString(0));
        foreach (var a in new[] { "issue.created", "issue.resolved", "issue.updated" }) Assert.Contains(a, actions);
    }
}
