using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Habita.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Habita.IntegrationTests;

/// <summary>Hito 10: Collector and Tenant roles, access codes, organization settings and private file storage.</summary>
public class Hito10Tests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    async Task<Guid> CreateTenantAsync(HttpClient owner, string name = "Inquilino Prueba", string? email = "contacto@example.com")
    {
        var r = await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = name, email, phone = "5512345678", notes = "Nota privada" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    async Task<string> CreateCodeAsync(HttpClient admin, string role, Guid? tenantId = null, int? validDays = null)
    {
        var r = await admin.PostAsJsonAsync("/api/v1/access-codes", new { role, tenantId, validDays });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("code").GetString()!;
    }

    async Task<HttpClient> RedeemAsync(string code)
    {
        var c = fx.NewClient();
        var r = await c.PostAsJsonAsync("/api/v1/auth/access-codes/redeem",
            new { code, email = $"canje-{Guid.NewGuid():N}@example.com", fullName = "Persona Canje", password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    async Task<(HttpClient Owner, HttpClient Tenant, Guid TenantId)> OrgWithTenantLoginAsync()
    {
        var (owner, _, _) = await fx.RegisterAsync("t10");
        var tenantId = await CreateTenantAsync(owner);
        return (owner, await RedeemAsync(await CreateCodeAsync(owner, "Tenant", tenantId)), tenantId);
    }

    // ---------- Access codes ----------

    [Fact]
    public async Task A_collector_code_creates_an_account_with_the_collector_role_and_works_only_once()
    {
        var (owner, orgId, _) = await fx.RegisterAsync("code");
        var code = await CreateCodeAsync(owner, "Collector");
        Assert.Matches(@"^[2-9A-HJKMNP-Z]{4}-[2-9A-HJKMNP-Z]{4}-[2-9A-HJKMNP-Z]{2}$", code);

        var collector = await RedeemAsync(code.ToLowerInvariant().Replace("-", " ")); // typed by hand: lower case and spaces still work
        var me = await Json(await collector.GetAsync("/api/v1/me"));
        Assert.Equal("Collector", me.GetProperty("activeRole").GetString());
        Assert.Equal(orgId, me.GetProperty("activeOrganizationId").GetGuid());

        var again = await fx.NewClient().PostAsJsonAsync("/api/v1/auth/access-codes/redeem",
            new { code, email = $"otro-{Guid.NewGuid():N}@example.com", fullName = "Otra", password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task Garbage_unknown_revoked_and_expired_codes_all_get_the_same_answer()
    {
        var (owner, _, _) = await fx.RegisterAsync("bad");
        var revoked = await CreateCodeAsync(owner, "Collector");
        var list = await Json(await owner.GetAsync("/api/v1/access-codes"));
        var id = list.EnumerateArray().First().GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/access-codes/{id}/revoke", null)).StatusCode);

        var expired = await CreateCodeAsync(owner, "Collector");
        using (var scope = fx.Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<HabitaDbContext>().Database
                .ExecuteSqlRawAsync("UPDATE \"AccessCodes\" SET \"ExpiresAt\" = now() - interval '1 minute' WHERE \"Id\" <> {0}", id);

        foreach (var code in new[] { "nope", "", "AAAA-AAAA-AA", "2222-2222-22", revoked, expired })
        {
            var r = await fx.NewClient().PostAsJsonAsync("/api/v1/auth/access-codes/redeem",
                new { code, email = $"x-{Guid.NewGuid():N}@example.com", fullName = "X", password = ApiFixture.Password });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("Código inválido o vencido.", (await Json(r)).GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task Two_people_redeeming_the_same_code_at_once_produce_exactly_one_membership()
    {
        var (owner, _, _) = await fx.RegisterAsync("race");
        var code = await CreateCodeAsync(owner, "Collector");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => fx.NewClient().PostAsJsonAsync("/api/v1/auth/access-codes/redeem",
            new { code, email = $"race-{i}-{Guid.NewGuid():N}@example.com", fullName = "Carrera", password = ApiFixture.Password })));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        var members = await Json(await owner.GetAsync("/api/v1/members"));
        Assert.Equal(2, members.GetArrayLength()); // the owner and the single collector
    }

    [Fact]
    public async Task A_tenant_code_links_the_login_to_that_tenant_record_and_only_one_login_per_tenant()
    {
        var (owner, tenant, tenantId) = await OrgWithTenantLoginAsync();
        var me = await Json(await tenant.GetAsync("/api/v1/me"));
        Assert.Equal("Tenant", me.GetProperty("activeRole").GetString());
        Assert.Equal(tenantId, me.GetProperty("activeTenantId").GetGuid());

        var second = await owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Tenant", tenantId });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode); // already has a login
    }

    [Fact]
    public async Task Code_creation_rules_are_enforced()
    {
        var (owner, _, _) = await fx.RegisterAsync("rules");
        var (otherOwner, _, _) = await fx.RegisterAsync("rules2");
        var tenantId = await CreateTenantAsync(owner);
        var foreignTenant = await CreateTenantAsync(otherOwner);

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Admin" })).StatusCode);     // only Collector/Tenant
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Tenant" })).StatusCode);    // needs a tenant
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Collector", tenantId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Tenant", tenantId = foreignTenant })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Collector", validDays = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Collector", validDays = 31 })).StatusCode);

        await owner.PatchAsJsonAsync($"/api/v1/tenants/{tenantId}", new { status = "Archived" });
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Tenant", tenantId })).StatusCode);
    }

    [Fact]
    public async Task Only_owner_and_admin_manage_codes_and_the_plain_code_is_never_listed_or_audited()
    {
        var (owner, _, _) = await fx.RegisterAsync("who");
        foreach (var role in new[] { "Finance", "Maintenance", "ReadOnly" })
        {
            var member = await fx.AddMemberAsync(owner, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/v1/access-codes", new { role = "Collector" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/v1/access-codes")).StatusCode);
        }
        var admin = await fx.AddMemberAsync(owner, "Admin");
        var code = await CreateCodeAsync(admin, "Collector");

        var listed = await (await owner.GetAsync("/api/v1/access-codes")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(code.Replace("-", ""), listed.ToUpperInvariant().Replace("-", ""));
        Assert.Contains("\"status\":\"Pending\"", listed);
        var audit = await (await owner.GetAsync("/api/v1/audit-logs?pageSize=100")).Content.ReadAsStringAsync();
        Assert.Contains("access_code.created", audit);
        Assert.DoesNotContain(code.Replace("-", ""), audit.ToUpperInvariant().Replace("-", ""));
    }

    [Fact]
    public async Task A_new_tenant_code_replaces_the_previous_unused_one()
    {
        var (owner, _, _) = await fx.RegisterAsync("replace");
        var tenantId = await CreateTenantAsync(owner);
        var first = await CreateCodeAsync(owner, "Tenant", tenantId);
        var second = await CreateCodeAsync(owner, "Tenant", tenantId);
        var r = await fx.NewClient().PostAsJsonAsync("/api/v1/auth/access-codes/redeem", new { code = first, email = $"a-{Guid.NewGuid():N}@example.com", fullName = "A", password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        await RedeemAsync(second);
    }

    [Fact]
    public async Task A_signed_in_user_can_use_a_code_to_join_another_organization_and_cannot_join_twice()
    {
        var (owner, orgId, _) = await fx.RegisterAsync("join");
        var (other, otherOrg, _) = await fx.RegisterAsync("join2");
        var code = await CreateCodeAsync(owner, "Collector");
        var joined = await other.PostAsJsonAsync("/api/v1/auth/access-codes/redeem", new { code });
        Assert.Equal(HttpStatusCode.NoContent, joined.StatusCode);
        var me = await Json(await other.GetAsync("/api/v1/me"));
        Assert.Equal(orgId, me.GetProperty("activeOrganizationId").GetGuid());
        Assert.Equal(2, me.GetProperty("organizations").GetArrayLength());

        var again = await CreateCodeAsync(owner, "Collector");
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsJsonAsync("/api/v1/auth/access-codes/redeem", new { code = again })).StatusCode);
        _ = otherOrg;
    }

    [Fact]
    public async Task An_existing_account_that_is_not_signed_in_is_asked_to_log_in_instead_of_being_taken_over()
    {
        var (owner, _, email) = await fx.RegisterAsync("exist");
        var code = await CreateCodeAsync(owner, "Collector");
        var r = await fx.NewClient().PostAsJsonAsync("/api/v1/auth/access-codes/redeem", new { code, email, fullName = "Intruso", password = "OtraPassw0rd!" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        // the code is still unused
        Assert.Equal("Pending", (await Json(await owner.GetAsync("/api/v1/access-codes"))).EnumerateArray().First().GetProperty("status").GetString());
    }

    [Fact]
    public async Task Code_redemption_is_rate_limited_per_client()
    {
        var limited = fx.Factory.WithWebHostBuilder(b => { b.UseSetting("RateLimiting:Enabled", "true"); b.UseSetting("RateLimiting:AuthPerMinute", "3"); }).CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            statuses.Add((await limited.PostAsJsonAsync("/api/v1/auth/access-codes/redeem", new { code = "2222-2222-22" })).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    // ---------- Roles ----------

    [Fact]
    public async Task Tenant_membership_cannot_be_handed_out_by_invitation_or_changed_into_staff()
    {
        var (owner, _, _) = await fx.RegisterAsync("tenantrole");
        var inv = await owner.PostAsJsonAsync("/api/v1/members/invitations", new { email = "x@example.com", role = "Tenant" });
        Assert.Equal(HttpStatusCode.Forbidden, inv.StatusCode);

        var tenantId = await CreateTenantAsync(owner);
        await RedeemAsync(await CreateCodeAsync(owner, "Tenant", tenantId));
        var member = (await Json(await owner.GetAsync("/api/v1/members"))).EnumerateArray().Single(m => m.GetProperty("role").GetString() == "Tenant");
        Assert.Equal(tenantId, member.GetProperty("tenantId").GetGuid());
        var toStaff = await owner.PatchAsJsonAsync($"/api/v1/members/{member.GetProperty("id").GetGuid()}", new { role = "Finance" });
        Assert.Equal(HttpStatusCode.BadRequest, toStaff.StatusCode);
        var toTenant = await owner.PatchAsJsonAsync($"/api/v1/members/{(await Json(await owner.GetAsync("/api/v1/members"))).EnumerateArray().First(m => m.GetProperty("role").GetString() == "Owner").GetProperty("id").GetGuid()}", new { role = "Tenant" });
        Assert.Equal(HttpStatusCode.Forbidden, toTenant.StatusCode);
    }

    [Fact]
    public async Task The_database_refuses_a_tenant_membership_without_a_tenant_record_and_staff_with_one()
    {
        var (owner, orgId, _) = await fx.RegisterAsync("dbcheck");
        var tenantId = await CreateTenantAsync(owner);
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HabitaDbContext>();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Memberships\" SET \"Role\" = 'Tenant' WHERE \"OrganizationId\" = {0}", orgId));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Memberships\" SET \"TenantId\" = {0} WHERE \"OrganizationId\" = {1}", tenantId, orgId));
    }

    [Fact]
    public async Task A_collector_collects_but_sees_nothing_else()
    {
        var (owner, _, _) = await fx.RegisterAsync("collector");
        var tenantId = await CreateTenantAsync(owner, "Marta Cobro", "marta@example.com");
        var collector = await RedeemAsync(await CreateCodeAsync(owner, "Collector"));

        // What a collector needs: tenants (without contact data), charges and payments.
        var tenants = await Json(await collector.GetAsync("/api/v1/tenants"));
        var t = tenants.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Marta Cobro", t.GetProperty("fullName").GetString());
        Assert.Equal(JsonValueKind.Null, t.GetProperty("email").ValueKind);
        Assert.Equal(JsonValueKind.Null, t.GetProperty("notes").ValueKind);
        Assert.Equal(0, (await Json(await collector.GetAsync("/api/v1/tenants?search=marta@example.com"))).GetProperty("total").GetInt32()); // cannot probe emails
        Assert.Equal(1, (await Json(await collector.GetAsync("/api/v1/tenants?search=Marta"))).GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await Json(await collector.GetAsync($"/api/v1/tenants/{tenantId}"))).GetProperty("email").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await collector.GetAsync("/api/v1/rent-charges")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await collector.GetAsync("/api/v1/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await collector.PostAsJsonAsync("/api/v1/payments", new { })).StatusCode); // reaches validation, not 403

        // Everything else is closed.
        foreach (var (method, url) in new[]
        {
            ("GET", "/api/v1/leases"), ("GET", "/api/v1/properties"), ("GET", "/api/v1/units"), ("GET", "/api/v1/dashboard"), ("GET", "/api/v1/maintenance-issues"),
            ("GET", "/api/v1/members"), ("GET", "/api/v1/audit-logs"), ("GET", "/api/v1/settings"), ("GET", "/api/v1/access-codes"),
            ("GET", "/api/v1/reports/financial-summary?from=2026-01-01&to=2026-12-31"), ("GET", "/api/v1/reports/balances.csv"),
            ("POST", "/api/v1/rent-charges"), ("POST", $"/api/v1/payments/{Guid.NewGuid()}/reverse"), ("POST", "/api/v1/tenants"), ("PATCH", $"/api/v1/tenants/{tenantId}"),
        })
            Assert.Equal(HttpStatusCode.Forbidden, (await collector.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) })).StatusCode);
    }

    [Fact]
    public async Task Every_endpoint_is_closed_to_a_tenant_login_except_the_few_it_needs()
    {
        var (_, tenant, _) = await OrgWithTenantLoginAsync();
        var endpoints = fx.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/v1")).ToList();
        Assert.True(endpoints.Count > 50, "the route list was not found");

        var open = new HashSet<string>
        {
            "GET /api/v1/me", "POST /api/v1/me/active-organization",
            "POST /api/v1/files", "GET /api/v1/files/{id:guid}",
        };
        var checkedCount = 0;
        foreach (var e in endpoints)
        {
            var raw = e.RoutePattern.RawText!;
            if (raw.StartsWith("/api/v1/auth/")) continue; // public by design, own tests
            foreach (var method in e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            {
                if (open.Contains($"{method} {raw.TrimEnd('/')}")) continue;
                if (raw.StartsWith("/api/v1/portal")) continue; // Hito 11: the tenant's own portal
                if (raw.StartsWith("/api/v1/notifications")) continue; // Hito 13: everyone reads their own notifications (own tests check it is only their own)
                var url = Regex.Replace(raw, @"\{[^}]*:guid[^}]*\}", _ => Guid.NewGuid().ToString());
                url = Regex.Replace(url, @"\{[^}]+\}", "1");
                var r = await tenant.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) });
                Assert.True(r.StatusCode == HttpStatusCode.Forbidden, $"{method} {raw} answered {(int)r.StatusCode} to a tenant login; it must be 403");
                checkedCount++;
            }
        }
        Assert.True(checkedCount > 40, $"only {checkedCount} endpoints were checked");
    }

    [Fact]
    public async Task Every_endpoint_requires_a_signed_in_user_except_the_public_auth_ones()
    {
        var anon = fx.NewClient();
        var endpoints = fx.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/v1") && !e.RoutePattern.RawText!.StartsWith("/api/v1/auth/")).ToList();
        foreach (var e in endpoints)
            foreach (var method in e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            {
                var url = Regex.Replace(Regex.Replace(e.RoutePattern.RawText!, @"\{[^}]*:guid[^}]*\}", _ => Guid.NewGuid().ToString()), @"\{[^}]+\}", "1");
                var r = await anon.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) });
                Assert.True(r.StatusCode == HttpStatusCode.Unauthorized, $"{method} {e.RoutePattern.RawText} answered {(int)r.StatusCode} without a session");
            }
    }

    // ---------- Settings ----------

    [Fact]
    public async Task Settings_have_defaults_can_be_updated_and_are_audited_without_content()
    {
        var (owner, _, _) = await fx.RegisterAsync("settings");
        var d = await Json(await owner.GetAsync("/api/v1/settings"));
        Assert.Equal("", d.GetProperty("landlordName").GetString());
        Assert.Equal(new[] { 3, 1, 0 }, d.GetProperty("rentReminderDays").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(new[] { 30, 7, 1 }, d.GetProperty("contractExpiryDays").EnumerateArray().Select(x => x.GetInt32()));

        var up = await owner.PatchAsJsonAsync("/api/v1/settings", new { landlordName = "  Alba Arrendadora  ", landlordAddress = "Calle 1", rentReminderDays = new[] { 0, 5, 5, 2 } });
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        var u = await Json(up);
        Assert.Equal("Alba Arrendadora", u.GetProperty("landlordName").GetString());
        Assert.Equal(new[] { 5, 2, 0 }, u.GetProperty("rentReminderDays").EnumerateArray().Select(x => x.GetInt32())); // sorted, de-duplicated
        Assert.Equal(new[] { 30, 7, 1 }, u.GetProperty("contractExpiryDays").EnumerateArray().Select(x => x.GetInt32())); // untouched

        var off = await Json(await owner.PatchAsJsonAsync("/api/v1/settings", new { rentReminderDays = Array.Empty<int>() }));
        Assert.Equal(0, off.GetProperty("rentReminderDays").GetArrayLength()); // an empty list turns the reminder off
        Assert.Equal("Alba Arrendadora", (await Json(await owner.GetAsync("/api/v1/settings"))).GetProperty("landlordName").GetString());

        var audit = await (await owner.GetAsync("/api/v1/audit-logs?pageSize=100")).Content.ReadAsStringAsync();
        Assert.Contains("settings.updated", audit);
        Assert.DoesNotContain("Alba Arrendadora", audit);
    }

    [Fact]
    public async Task Invalid_settings_are_rejected_and_nothing_is_saved()
    {
        var (owner, _, _) = await fx.RegisterAsync("settingsbad");
        foreach (var body in new object[]
        {
            new { rentReminderDays = new[] { -1 } }, new { rentReminderDays = new[] { 61 } }, new { rentReminderDays = new[] { 1, 2, 3, 4, 5, 6 } },
            new { contractExpiryDays = new[] { 0 } }, new { landlordName = new string('a', 201) }, new { landlordAddress = new string('a', 501) },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync("/api/v1/settings", body)).StatusCode);
        Assert.Equal("", (await Json(await owner.GetAsync("/api/v1/settings"))).GetProperty("landlordName").GetString());
    }

    [Fact]
    public async Task Settings_are_per_organization_and_closed_to_everyone_but_owner_and_admin()
    {
        var (a, _, _) = await fx.RegisterAsync("setA");
        var (b, _, _) = await fx.RegisterAsync("setB");
        await a.PatchAsJsonAsync("/api/v1/settings", new { landlordName = "Solo de A" });
        Assert.Equal("", (await Json(await b.GetAsync("/api/v1/settings"))).GetProperty("landlordName").GetString());
        foreach (var role in new[] { "Finance", "Maintenance", "ReadOnly" })
        {
            var m = await fx.AddMemberAsync(a, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await m.GetAsync("/api/v1/settings")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await m.PatchAsJsonAsync("/api/v1/settings", new { landlordName = "x" })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await (await fx.AddMemberAsync(a, "Admin")).GetAsync("/api/v1/settings")).StatusCode);
    }

    // ---------- Files ----------

    static MultipartFormDataContent FileContent(byte[] bytes, string name, string contentType = "image/png")
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        // Written by hand so a hostile file name reaches the server exactly as an attacker would send it.
        part.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"file\"; filename=\"{name}\"");
        return new MultipartFormDataContent { part };
    }

    [Fact]
    public async Task An_uploaded_file_round_trips_with_download_hardening_headers()
    {
        var (owner, _, _) = await fx.RegisterAsync("file");
        var up = await owner.PostAsync("/api/v1/files", FileContent(Png, "comprobante.png"));
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var meta = await Json(up);
        Assert.Equal("image/png", meta.GetProperty("contentType").GetString());
        Assert.Equal(Png.Length, meta.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Png)), meta.GetProperty("sha256").GetString());

        var down = await owner.GetAsync($"/api/v1/files/{meta.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.OK, down.StatusCode);
        Assert.Equal(Png, await down.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", down.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", down.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", down.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task The_file_type_comes_from_the_bytes_and_not_from_the_name_or_the_declared_type()
    {
        var (owner, _, _) = await fx.RegisterAsync("sniff");
        var html = System.Text.Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/v1/files", FileContent(html, "foto.png", "image/png"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/v1/files", FileContent(html, "foto.jpg", "image/jpeg"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/v1/files", FileContent([0x4D, 0x5A, 0x90, 0x00, 1, 2, 3], "programa.pdf", "application/pdf"))).StatusCode); // an .exe
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/v1/files", FileContent([], "vacio.png"))).StatusCode);

        // A PNG uploaded as "document.pdf" is stored as what it really is.
        var ok = await Json(await owner.PostAsync("/api/v1/files", FileContent(Png, "documento.pdf", "application/pdf")));
        Assert.Equal("image/png", ok.GetProperty("contentType").GetString());
        var pdf = await owner.PostAsync("/api/v1/files", FileContent(System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF"), "contrato.pdf", "application/pdf"));
        Assert.Equal("application/pdf", (await Json(pdf)).GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task Files_over_5_MB_are_refused_and_much_larger_requests_never_get_read()
    {
        var (owner, _, _) = await fx.RegisterAsync("big");
        byte[] Big(int size) { var b = new byte[size]; Png.CopyTo(b, 0); return b; }
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync("/api/v1/files", FileContent(Big(5 * 1024 * 1024), "justo.png"))).StatusCode is HttpStatusCode.Created ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/v1/files", FileContent(Big(5 * 1024 * 1024 + 1), "pasado.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await owner.PostAsync("/api/v1/files", FileContent(Big(7 * 1024 * 1024), "enorme.png"))).StatusCode);
        // the larger cap applies to uploads only
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await owner.PostAsync("/api/v1/tenants", new ByteArrayContent(new byte[2_000_000]) { Headers = { ContentType = new("application/json") } })).StatusCode);
    }

    [Fact]
    public async Task A_display_name_can_never_become_a_path_or_a_header_injection()
    {
        var (owner, _, _) = await fx.RegisterAsync("name");
        var meta = await Json(await owner.PostAsync("/api/v1/files", FileContent(Png, "../../etc/pass<>wd.png")));
        var name = meta.GetProperty("name").GetString()!;
        Assert.DoesNotContain("/", name); Assert.DoesNotContain("..", name); Assert.DoesNotContain("<", name);
        Assert.EndsWith(".png", name);
        Assert.StartsWith("archivo", (await Json(await owner.PostAsync("/api/v1/files", FileContent(Png, "..")))).GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_file_is_visible_to_its_uploader_and_to_reviewing_staff_and_to_no_one_else()
    {
        var (owner, tenantA, _) = await OrgWithTenantLoginAsync();
        var tenantB = await RedeemAsync(await CreateCodeAsync(owner, "Tenant", await CreateTenantAsync(owner, "Otra Persona", null)));
        var (strangerOwner, _, _) = await fx.RegisterAsync("stranger");

        var id = (await Json(await tenantA.PostAsync("/api/v1/files", FileContent(Png, "mio.png")))).GetProperty("id").GetGuid();
        var url = $"/api/v1/files/{id}";
        Assert.Equal(HttpStatusCode.OK, (await tenantA.GetAsync(url)).StatusCode);                 // uploader
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(url)).StatusCode);                   // Owner
        Assert.Equal(HttpStatusCode.OK, (await (await fx.AddMemberAsync(owner, "Admin")).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await fx.AddMemberAsync(owner, "Finance")).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await RedeemAsync(await CreateCodeAsync(owner, "Collector"))).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tenantB.GetAsync(url)).StatusCode);           // another tenant of the SAME organization
        Assert.Equal(HttpStatusCode.NotFound, (await (await fx.AddMemberAsync(owner, "Maintenance")).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await fx.AddMemberAsync(owner, "ReadOnly")).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await strangerOwner.GetAsync(url)).StatusCode);     // another organization
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.NewClient().GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/files/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Uploads_are_audited_without_the_file_name_and_stored_outside_any_served_folder()
    {
        var (owner, orgId, _) = await fx.RegisterAsync("fileaudit");
        await owner.PostAsync("/api/v1/files", FileContent(Png, "secreto-banco.png"));
        var audit = await (await owner.GetAsync("/api/v1/audit-logs?pageSize=100")).Content.ReadAsStringAsync();
        Assert.Contains("file.uploaded", audit);
        Assert.DoesNotContain("secreto-banco", audit);
        // stored under a GUID path, not under the user's file name
        var root = fx.Factory.Services.GetRequiredService<IConfiguration>()["Storage:Root"]!;
        var stored = Directory.GetFiles(Path.Combine(root, orgId.ToString("N")));
        Assert.Single(stored);
        Assert.DoesNotContain("secreto", stored[0]);
    }

    [Fact]
    public async Task Uploads_are_rate_limited()
    {
        var limited = fx.Factory.WithWebHostBuilder(b => { b.UseSetting("RateLimiting:Enabled", "true"); b.UseSetting("RateLimiting:UploadsPerMinute", "2"); }).CreateClient();
        var (_, _, email) = await fx.RegisterAsync("ratefile");
        await limited.PostAsJsonAsync("/api/v1/auth/login", new { email, password = ApiFixture.Password });
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) statuses.Add((await limited.PostAsync("/api/v1/files", FileContent(Png, "a.png"))).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
