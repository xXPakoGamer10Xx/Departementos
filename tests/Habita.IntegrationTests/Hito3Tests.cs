using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Habita.IntegrationTests;

public class Hito3Tests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    static async Task<Guid> CreateTenant(HttpClient c, string name = "Ana Ejemplo", string? email = "ana@example.com", string? phone = "55 1234 5678")
    {
        var r = await c.PostAsJsonAsync("/api/v1/tenants", new { fullName = name, email, phone });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Create_get_update_and_normalize_contact_data()
    {
        var (owner, _, _) = await fx.RegisterAsync("ten");
        var id = await CreateTenant(owner, "  Ana Ejemplo ", "  ANA@Example.COM ", " 55 1234 5678 ");

        var t = await Json(await owner.GetAsync($"/api/v1/tenants/{id}"));
        Assert.Equal("Ana Ejemplo", t.GetProperty("fullName").GetString());
        Assert.Equal("ana@example.com", t.GetProperty("email").GetString());
        Assert.Equal("55 1234 5678", t.GetProperty("phone").GetString());
        Assert.Equal("Active", t.GetProperty("status").GetString());

        var upd = await Json(await owner.PatchAsJsonAsync($"/api/v1/tenants/{id}", new { phone = "", notes = "Paga a tiempo" }));
        Assert.Equal(JsonValueKind.Null, upd.GetProperty("phone").ValueKind); // empty string clears
        Assert.Equal("ana@example.com", upd.GetProperty("email").GetString()); // absent = unchanged
        Assert.Equal("Paga a tiempo", upd.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task Email_and_phone_are_optional()
    {
        var (owner, _, _) = await fx.RegisterAsync("opt");
        var r = await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Luis Prueba" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    [Fact]
    public async Task Search_by_name_email_or_phone_with_status_filter_and_pagination()
    {
        var (owner, _, _) = await fx.RegisterAsync("tsearch");
        var ana = await CreateTenant(owner, "Ana Ejemplo", "ana@mail.com", "+52 (55) 1111-2222");
        await CreateTenant(owner, "Luis Prueba", "luis@correo.mx", "81 9999 0000");
        await CreateTenant(owner, "María Soto", null, null);

        async Task<int> Total(string q) => (await Json(await owner.GetAsync($"/api/v1/tenants?{q}"))).GetProperty("total").GetInt32();
        Assert.Equal(1, await Total("search=ANA"));
        Assert.Equal(1, await Total("search=correo.mx"));
        Assert.Equal(1, await Total("search=9999"));
        Assert.Equal(0, await Total("search=zzz"));
        Assert.Equal(3, await Total(""));

        await owner.PatchAsJsonAsync($"/api/v1/tenants/{ana}", new { status = "Archived" });
        Assert.Equal(2, await Total("status=Active"));
        Assert.Equal(1, await Total("status=Archived"));
        var page = await Json(await owner.GetAsync("/api/v1/tenants?pageSize=1&page=2&status=Active"));
        Assert.Single(page.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData("", "a@b.com", "5512345678")]
    [InlineData("X", "no-es-correo", "5512345678")]
    [InlineData("X", "a@b", "5512345678")]
    [InlineData("X", "a b@c.com", "5512345678")]
    [InlineData("X", "a@b.com", "123")]
    [InlineData("X", "a@b.com", "abcdefghij")]
    public async Task Validation_rejects_bad_input(string name, string email, string phone)
    {
        var (owner, _, _) = await fx.RegisterAsync("tval");
        var r = await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = name, email, phone });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Archive_and_restore_keep_the_record()
    {
        var (owner, _, _) = await fx.RegisterAsync("tarch");
        var id = await CreateTenant(owner);
        var a = await Json(await owner.PatchAsJsonAsync($"/api/v1/tenants/{id}", new { status = "Archived", reason = "Se mudó" }));
        Assert.Equal("Archived", a.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/v1/tenants/{id}")).StatusCode); // still retrievable: never physically deleted
        var r = await Json(await owner.PatchAsJsonAsync($"/api/v1/tenants/{id}", new { status = "Active" }));
        Assert.Equal("Active", r.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OrganizationA_cannot_read_or_modify_organizationB_tenants()
    {
        var (a, _, _) = await fx.RegisterAsync("tiso-a");
        var (b, _, _) = await fx.RegisterAsync("tiso-b");
        var idB = await CreateTenant(b, "Cliente de B");

        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/v1/tenants/{idB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.PatchAsJsonAsync($"/api/v1/tenants/{idB}", new { fullName = "Hackeado" })).StatusCode);
        Assert.Equal(0, (await Json(await a.GetAsync("/api/v1/tenants?search=Cliente"))).GetProperty("total").GetInt32());
        Assert.Equal("Cliente de B", (await Json(await b.GetAsync($"/api/v1/tenants/{idB}"))).GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task Roles_Owner_Admin_write_Finance_ReadOnly_read_and_Maintenance_has_no_access()
    {
        var (owner, _, _) = await fx.RegisterAsync("troles");
        var id = await CreateTenant(owner);
        var admin = await fx.AddMemberAsync(owner, "Admin");
        var finance = await fx.AddMemberAsync(owner, "Finance");
        var readOnly = await fx.AddMemberAsync(owner, "ReadOnly");
        var maintenance = await fx.AddMemberAsync(owner, "Maintenance");

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Hecho por Admin" })).StatusCode);
        foreach (var c in new[] { finance, readOnly })
        {
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/tenants")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/v1/tenants/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/v1/tenants", new { fullName = "No" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PatchAsJsonAsync($"/api/v1/tenants/{id}", new { fullName = "No" })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await maintenance.GetAsync("/api/v1/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maintenance.GetAsync($"/api/v1/tenants/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.NewClient().GetAsync("/api/v1/tenants")).StatusCode);
    }

    [Fact]
    public async Task Audit_records_tenant_changes_without_personal_data()
    {
        var (owner, orgId, _) = await fx.RegisterAsync("taud");
        var id = await CreateTenant(owner, "Persona Privada", "privada@example.com", "5598765432");
        await owner.PatchAsJsonAsync($"/api/v1/tenants/{id}", new { email = "otra@example.com", notes = "secreto" });
        await owner.PatchAsJsonAsync($"/api/v1/tenants/{id}", new { status = "Archived", reason = "Fin de contrato" });

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT "Action", "Reason", COALESCE("Metadata"::text,'') FROM "AuditLogs" WHERE "OrganizationId" = @o AND "EntityType" = 'Tenant'""", conn);
        cmd.Parameters.AddWithValue("o", orgId);
        var rows = new List<(string Action, string? Reason, string Meta)>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) rows.Add((rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.GetString(2)));

        Assert.Contains(rows, r => r.Action == "tenant.created");
        Assert.Contains(rows, r => r.Action == "tenant.updated" && r.Meta.Contains("email") && r.Meta.Contains("notes"));
        Assert.Contains(rows, r => r.Action == "tenant.archived" && r.Reason == "Fin de contrato");
        var everything = string.Join(' ', rows.Select(r => r.Meta + r.Reason));
        foreach (var pii in new[] { "Persona Privada", "privada@example.com", "otra@example.com", "5598765432", "secreto" })
            Assert.DoesNotContain(pii, everything);
    }
}
