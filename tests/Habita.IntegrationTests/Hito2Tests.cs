using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Habita.IntegrationTests;

public class Hito2Tests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    static async Task<Guid> CreateProperty(HttpClient c, string name = "Torre Norte", string address = "Av. Reforma 100, CDMX")
    {
        var r = await c.PostAsJsonAsync("/api/v1/properties", new { name, address });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    static Task<HttpResponseMessage> CreateUnit(HttpClient c, Guid propertyId, string code, decimal rent = 8500m, string type = "Apartment") =>
        c.PostAsJsonAsync($"/api/v1/properties/{propertyId}/units", new { code, type, bedrooms = 2, baseRent = rent });

    [Fact]
    public async Task Owner_creates_property_and_units_and_lists_them_with_counts()
    {
        var (owner, _, _) = await fx.RegisterAsync("props");
        var pid = await CreateProperty(owner);
        foreach (var code in new[] { "101", "102", "201" }) Assert.Equal(HttpStatusCode.Created, (await CreateUnit(owner, pid, code)).StatusCode);

        var list = await Json(await owner.GetAsync("/api/v1/properties"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(3, list.GetProperty("items")[0].GetProperty("unitCount").GetInt32());

        var units = await Json(await owner.GetAsync($"/api/v1/properties/{pid}/units"));
        Assert.Equal(3, units.GetProperty("total").GetInt32());
        Assert.Equal(8500m, units.GetProperty("items")[0].GetProperty("baseRent").GetDecimal());
        Assert.Equal("Available", units.GetProperty("items")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Search_filter_and_pagination()
    {
        var (owner, _, _) = await fx.RegisterAsync("search");
        await CreateProperty(owner, "Torre Norte", "Calle Uno");
        await CreateProperty(owner, "Casa Jardín", "Calle Dos");
        await CreateProperty(owner, "Edificio Sur", "Calle Tres");

        Assert.Equal(1, (await Json(await owner.GetAsync("/api/v1/properties?search=jard"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await owner.GetAsync("/api/v1/properties?search=TRES"))).GetProperty("total").GetInt32());
        var page2 = await Json(await owner.GetAsync("/api/v1/properties?pageSize=2&page=2"));
        Assert.Equal(3, page2.GetProperty("total").GetInt32());
        Assert.Single(page2.GetProperty("items").EnumerateArray());
        Assert.Equal(100, (await Json(await owner.GetAsync("/api/v1/properties?pageSize=5000"))).GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Unit_code_is_unique_per_property_case_insensitive_but_reusable_across_properties()
    {
        var (owner, _, _) = await fx.RegisterAsync("dup");
        var a = await CreateProperty(owner, "A", "Dir A");
        var b = await CreateProperty(owner, "B", "Dir B");
        Assert.Equal(HttpStatusCode.Created, (await CreateUnit(owner, a, "a-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await CreateUnit(owner, a, "A-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateUnit(owner, b, "A-1")).StatusCode);
        Assert.Equal("A-1", (await Json(await owner.GetAsync($"/api/v1/properties/{a}/units"))).GetProperty("items")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Concurrent_creation_of_the_same_code_yields_exactly_one_unit()
    {
        var (owner, _, _) = await fx.RegisterAsync("race");
        var pid = await CreateProperty(owner);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => CreateUnit(owner, pid, "X-1")));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }

    [Fact]
    public async Task Validation_rejects_bad_input()
    {
        var (owner, _, _) = await fx.RegisterAsync("val");
        var pid = await CreateProperty(owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/properties", new { name = " ", address = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateUnit(owner, pid, "U1", rent: -1m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateUnit(owner, pid, "U1", rent: 10.123m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateUnit(owner, pid, "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.PostAsJsonAsync($"/api/v1/properties/{pid}/units", new { code = "U2", bedrooms = -3, baseRent = 1 })).StatusCode);
    }

    [Fact]
    public async Task Occupied_cannot_be_set_manually_and_maintenance_status_round_trips()
    {
        var (owner, _, _) = await fx.RegisterAsync("status");
        var pid = await CreateProperty(owner);
        var unit = await Json(await CreateUnit(owner, pid, "301"));
        var uid = unit.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync($"/api/v1/properties/{pid}/units/{uid}", new { status = "Occupied" })).StatusCode);
        var m = await Json(await owner.PatchAsJsonAsync($"/api/v1/properties/{pid}/units/{uid}", new { status = "Maintenance", baseRent = 9000 }));
        Assert.Equal("Maintenance", m.GetProperty("status").GetString());
        Assert.Equal(9000m, m.GetProperty("baseRent").GetDecimal());
    }

    [Fact]
    public async Task Archiving_a_property_archives_its_units_blocks_new_ones_and_restore_keeps_units_archived()
    {
        var (owner, _, _) = await fx.RegisterAsync("arch");
        var pid = await CreateProperty(owner);
        await CreateUnit(owner, pid, "1");
        await CreateUnit(owner, pid, "2");

        var archive = await owner.PatchAsJsonAsync($"/api/v1/properties/{pid}", new { status = "Archived", reason = "Venta" });
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.Equal(0, (await Json(archive)).GetProperty("unitCount").GetInt32());

        var units = await Json(await owner.GetAsync($"/api/v1/properties/{pid}/units"));
        Assert.All(units.GetProperty("items").EnumerateArray(), u => Assert.Equal("Archived", u.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await CreateUnit(owner, pid, "3")).StatusCode);
        var firstUnit = units.GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PatchAsJsonAsync($"/api/v1/properties/{pid}/units/{firstUnit}", new { status = "Available" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"/api/v1/properties/{pid}", new { status = "Active" })).StatusCode);
        var after = await Json(await owner.GetAsync($"/api/v1/properties/{pid}/units"));
        Assert.All(after.GetProperty("items").EnumerateArray(), u => Assert.Equal("Archived", u.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"/api/v1/properties/{pid}/units/{firstUnit}", new { status = "Available" })).StatusCode);
    }

    [Fact]
    public async Task OrganizationA_cannot_read_or_modify_organizationB_properties_or_units()
    {
        var (a, _, _) = await fx.RegisterAsync("iso-a");
        var (b, _, _) = await fx.RegisterAsync("iso-b");
        var pidB = await CreateProperty(b, "Privada B", "Dir B");
        var uidB = (await Json(await CreateUnit(b, pidB, "B1"))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/v1/properties/{pidB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/v1/properties/{pidB}/units")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.PatchAsJsonAsync($"/api/v1/properties/{pidB}", new { name = "Hackeada" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CreateUnit(a, pidB, "A9")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.PatchAsJsonAsync($"/api/v1/properties/{pidB}/units/{uidB}", new { baseRent = 1 })).StatusCode);
        Assert.Equal(0, (await Json(await a.GetAsync("/api/v1/properties"))).GetProperty("total").GetInt32());

        var intact = await Json(await b.GetAsync($"/api/v1/properties/{pidB}"));
        Assert.Equal("Privada B", intact.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Database_rejects_a_unit_that_points_to_another_organizations_property()
    {
        var (a, orgA, _) = await fx.RegisterAsync("fk-a");
        var (b, _, _) = await fx.RegisterAsync("fk-b");
        var pidB = await CreateProperty(b);

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "Units" ("Id","OrganizationId","PropertyId","Code","Type","BaseRent","Status","CreatedAt","UpdatedAt")
            VALUES (gen_random_uuid(), @org, @prop, 'ZZ', 'Apartment', 1, 'Available', now(), now())
            """, conn);
        cmd.Parameters.AddWithValue("org", orgA);
        cmd.Parameters.AddWithValue("prop", pidB);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23503", ex.SqlState); // foreign_key_violation
    }

    [Fact]
    public async Task Roles_Finance_and_ReadOnly_can_read_but_not_write_and_Maintenance_never_sees_rent()
    {
        var (owner, _, _) = await fx.RegisterAsync("roles2");
        var pid = await CreateProperty(owner);
        await CreateUnit(owner, pid, "101");
        var finance = await fx.AddMemberAsync(owner, "Finance");
        var readOnly = await fx.AddMemberAsync(owner, "ReadOnly");
        var maintenance = await fx.AddMemberAsync(owner, "Maintenance");
        var admin = await fx.AddMemberAsync(owner, "Admin");

        foreach (var c in new[] { finance, readOnly })
        {
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/properties")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/v1/properties", new { name = "x", address = "y" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await CreateUnit(c, pid, "Z1")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PatchAsJsonAsync($"/api/v1/properties/{pid}", new { name = "x" })).StatusCode);
        }
        Assert.Equal(8500m, (await Json(await finance.GetAsync($"/api/v1/properties/{pid}/units"))).GetProperty("items")[0].GetProperty("baseRent").GetDecimal());

        var mUnits = await maintenance.GetAsync($"/api/v1/properties/{pid}/units");
        Assert.Equal(HttpStatusCode.OK, mUnits.StatusCode);
        var item = (await Json(mUnits)).GetProperty("items")[0];
        Assert.Equal("101", item.GetProperty("code").GetString());
        Assert.False(item.TryGetProperty("baseRent", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateUnit(maintenance, pid, "M1")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await CreateUnit(admin, pid, "A1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.NewClient().GetAsync("/api/v1/properties")).StatusCode);
    }

    [Fact]
    public async Task Property_and_unit_changes_are_audited()
    {
        var (owner, orgId, _) = await fx.RegisterAsync("aud2");
        var pid = await CreateProperty(owner);
        var uid = (await Json(await CreateUnit(owner, pid, "7"))).GetProperty("id").GetGuid();
        await owner.PatchAsJsonAsync($"/api/v1/properties/{pid}/units/{uid}", new { baseRent = 9100 });
        await owner.PatchAsJsonAsync($"/api/v1/properties/{pid}", new { status = "Archived", reason = "Venta" });

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT "Action", "Reason" FROM "AuditLogs" WHERE "OrganizationId" = @o AND "EntityType" IN ('Property','Unit')""", conn);
        cmd.Parameters.AddWithValue("o", orgId);
        var actions = new List<(string, string?)>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) actions.Add((rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1)));
        Assert.Contains(actions, a => a.Item1 == "property.created");
        Assert.Contains(actions, a => a.Item1 == "unit.created");
        Assert.Contains(actions, a => a.Item1 == "unit.updated");
        Assert.Contains(actions, a => a.Item1 == "property.archived" && a.Item2 == "Venta");
    }
}
