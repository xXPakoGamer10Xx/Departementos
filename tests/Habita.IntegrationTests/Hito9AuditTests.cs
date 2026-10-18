using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Habita.IntegrationTests;

public class Hito9AuditTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    static string D(DateOnly d) => d.ToString("yyyy-MM-dd");
    static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City")).Date);
    static async Task<List<JsonElement>> Items(HttpClient c, string query = "") => (await Json(await c.GetAsync($"/api/v1/audit-logs?{query}"))).GetProperty("items").EnumerateArray().ToList();

    [Fact]
    public async Task Owner_sees_entries_newest_first_with_labels_actors_reasons_and_metadata()
    {
        var (owner, _, _) = await fx.RegisterAsync("aud9");
        var pid = (await Json(await owner.PostAsJsonAsync("/api/v1/properties", new { name = "Torre Norte", address = "Reforma 100" }))).GetProperty("id").GetGuid();
        await owner.PatchAsJsonAsync($"/api/v1/properties/{pid}", new { status = "Archived", reason = "Venta del inmueble" });

        var items = await Items(owner);
        Assert.Equal("property.archived", items[0].GetProperty("action").GetString()); // newest first
        Assert.Equal("Archivó una propiedad", items[0].GetProperty("label").GetString());
        Assert.Equal("Usuario Prueba", items[0].GetProperty("actorName").GetString());
        Assert.Equal("Venta del inmueble", items[0].GetProperty("reason").GetString());
        Assert.Equal(pid, items[0].GetProperty("entityId").GetGuid());
        Assert.Equal(JsonValueKind.Object, items[0].GetProperty("metadata").ValueKind);   // structured, not an opaque string
        Assert.Contains(items, i => i.GetProperty("action").GetString() == "organization.created");
        Assert.Contains(items, i => i.GetProperty("action").GetString() == "property.created");
    }

    [Fact]
    public async Task Filters_by_entity_type_action_actor_entity_date_and_paginates()
    {
        var (owner, _, _) = await fx.RegisterAsync("audf");
        var admin = await fx.AddMemberAsync(owner, "Admin");
        var pid = (await Json(await owner.PostAsJsonAsync("/api/v1/properties", new { name = "Torre", address = "x" }))).GetProperty("id").GetGuid();
        var tid = (await Json(await admin.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Ana Ejemplo" }))).GetProperty("id").GetGuid();
        var adminId = (await Json(await admin.GetAsync("/api/v1/me"))).GetProperty("user").GetProperty("id").GetGuid();

        Assert.All(await Items(owner, "entityType=Tenant"), i => Assert.Equal("Tenant", i.GetProperty("entityType").GetString()));
        Assert.Single(await Items(owner, "entityType=Tenant"));
        Assert.All(await Items(owner, "action=property."), i => Assert.StartsWith("property.", i.GetProperty("action").GetString()));
        Assert.Single(await Items(owner, $"entityId={tid}"));
        var byAdmin = await Items(owner, $"actorUserId={adminId}");
        Assert.NotEmpty(byAdmin);
        Assert.All(byAdmin, i => Assert.Equal(adminId, i.GetProperty("actorUserId").GetGuid()));
        Assert.Contains(await Items(owner, $"entityId={pid}"), i => i.GetProperty("action").GetString() == "property.created");

        Assert.NotEmpty(await Items(owner, $"from={D(Today)}&to={D(Today)}"));              // everything happened today
        Assert.Empty(await Items(owner, "from=2020-01-01&to=2020-01-31"));                  // nothing in the past
        Assert.Empty(await Items(owner, $"from={D(Today.AddDays(1))}"));                    // nothing in the future

        var total = (await Json(await owner.GetAsync("/api/v1/audit-logs"))).GetProperty("total").GetInt32();
        var page2 = await Json(await owner.GetAsync("/api/v1/audit-logs?pageSize=2&page=2"));
        Assert.Equal(total, page2.GetProperty("total").GetInt32());
        Assert.Equal(2, page2.GetProperty("items").GetArrayLength());
        Assert.Equal(100, (await Json(await owner.GetAsync("/api/v1/audit-logs?pageSize=9999"))).GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Only_Owner_and_Admin_can_read_the_audit_log()
    {
        var (owner, _, _) = await fx.RegisterAsync("audperm");
        var admin = await fx.AddMemberAsync(owner, "Admin");
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/audit-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/audit-logs")).StatusCode);
        foreach (var role in new[] { "Finance", "ReadOnly", "Maintenance" })
            Assert.Equal(HttpStatusCode.Forbidden, (await (await fx.AddMemberAsync(owner, role)).GetAsync("/api/v1/audit-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.NewClient().GetAsync("/api/v1/audit-logs")).StatusCode);
    }

    [Fact]
    public async Task Organizations_only_see_their_own_audit_entries()
    {
        var (a, _, _) = await fx.RegisterAsync("audisoA");
        var (b, _, _) = await fx.RegisterAsync("audisoB");
        var pidA = (await Json(await a.PostAsJsonAsync("/api/v1/properties", new { name = "Privada de A", address = "x" }))).GetProperty("id").GetGuid();

        Assert.Empty((await Items(b, $"entityId={pidA}")));
        Assert.DoesNotContain("Privada de A", await (await b.GetAsync("/api/v1/audit-logs?pageSize=100")).Content.ReadAsStringAsync());
        Assert.All(await Items(b), i => Assert.NotEqual(pidA, i.GetProperty("entityId").GetGuid()));
        Assert.Contains(await Items(a, $"entityId={pidA}"), i => i.GetProperty("action").GetString() == "property.created");
    }

    [Fact]
    public async Task The_audit_log_cannot_be_changed_through_the_api_and_never_holds_secrets()
    {
        var (owner, _, email) = await fx.RegisterAsync("audsec");
        // Exercise the flows that handle secrets: invitations (tokens), password reset, login.
        var inv = await owner.PostAsJsonAsync("/api/v1/members/invitations", new { email = "nueva@example.com", role = "ReadOnly" });
        var token = (await Json(inv)).GetProperty("token").GetString()!;
        await fx.NewClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });
        await fx.NewClient().PostAsJsonAsync("/api/v1/auth/login", new { email, password = ApiFixture.Password });

        var id = (await Items(owner))[0].GetProperty("id").GetGuid();
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            var r = await owner.SendAsync(new HttpRequestMessage(method, $"/api/v1/audit-logs/{id}") { Content = JsonContent.Create(new { action = "x" }) });
            Assert.True(r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} returned {r.StatusCode}");
        }
        var raw = await (await owner.GetAsync("/api/v1/audit-logs?pageSize=100")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, raw);
        Assert.DoesNotContain(ApiFixture.Password, raw);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TokenHash", raw);
    }
}
