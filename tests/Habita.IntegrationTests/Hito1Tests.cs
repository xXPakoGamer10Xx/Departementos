using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Habita.IntegrationTests;

public class Hito1Tests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Private_endpoints_require_authentication()
    {
        var r = await fx.NewClient().GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Register_creates_owner_and_active_organization()
    {
        var (client, orgId, email) = await fx.RegisterAsync("reg");
        var me = await Json(await client.GetAsync("/api/v1/me"));
        Assert.Equal(email, me.GetProperty("user").GetProperty("email").GetString());
        Assert.Equal(orgId, me.GetProperty("activeOrganizationId").GetGuid());
        Assert.Equal("Owner", me.GetProperty("activeRole").GetString());
    }

    [Fact]
    public async Task Criterion1_OrganizationA_cannot_read_or_modify_organizationB()
    {
        var (a, orgA, _) = await fx.RegisterAsync("a");
        var (b, orgB, _) = await fx.RegisterAsync("b");

        var membersB = await Json(await b.GetAsync("/api/v1/members"));
        var memberBId = membersB[0].GetProperty("id").GetGuid();

        var membersA = await Json(await a.GetAsync("/api/v1/members"));
        Assert.Single(membersA.EnumerateArray());
        Assert.DoesNotContain(membersA.EnumerateArray(), m => m.GetProperty("id").GetGuid() == memberBId);

        // Knowing B's UUIDs grants nothing.
        var patch = await a.PatchAsJsonAsync($"/api/v1/members/{memberBId}", new { role = "ReadOnly" });
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        var swap = await a.PostAsJsonAsync("/api/v1/me/active-organization", new { organizationId = orgB });
        Assert.Equal(HttpStatusCode.NotFound, swap.StatusCode);

        // B is untouched.
        var after = await Json(await b.GetAsync("/api/v1/members"));
        Assert.Equal("Owner", after[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Admin_cannot_assign_owner_or_modify_owner_and_finance_cannot_manage_users()
    {
        var (owner, _, _) = await fx.RegisterAsync("roles");
        var admin = await fx.AddMemberAsync(owner, "Admin");
        var finance = await fx.AddMemberAsync(owner, "Finance");

        var inviteOwner = await admin.PostAsJsonAsync("/api/v1/members/invitations", new { email = "x@example.com", role = "Owner" });
        Assert.Equal(HttpStatusCode.Forbidden, inviteOwner.StatusCode);

        var ownerId = (await Json(await owner.GetAsync("/api/v1/members")))
            .EnumerateArray().First(m => m.GetProperty("role").GetString() == "Owner").GetProperty("id").GetGuid();
        var demote = await admin.PatchAsJsonAsync($"/api/v1/members/{ownerId}", new { role = "ReadOnly" });
        Assert.Equal(HttpStatusCode.Forbidden, demote.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await finance.GetAsync("/api/v1/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await finance.PostAsJsonAsync("/api/v1/members/invitations", new { email = "y@example.com", role = "ReadOnly" })).StatusCode);
    }

    [Fact]
    public async Task Last_owner_cannot_be_demoted_or_deactivated()
    {
        var (owner, _, _) = await fx.RegisterAsync("last");
        var ownerId = (await Json(await owner.GetAsync("/api/v1/members")))[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync($"/api/v1/members/{ownerId}", new { role = "Admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync($"/api/v1/members/{ownerId}", new { status = "Deactivated" })).StatusCode);
    }

    [Fact]
    public async Task Deactivated_member_loses_access_immediately()
    {
        var (owner, _, _) = await fx.RegisterAsync("deact");
        var admin = await fx.AddMemberAsync(owner, "Admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/members")).StatusCode);

        var adminMemberId = (await Json(await owner.GetAsync("/api/v1/members")))
            .EnumerateArray().First(m => m.GetProperty("role").GetString() == "Admin").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"/api/v1/members/{adminMemberId}", new { status = "Deactivated" })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/members")).StatusCode);
    }

    [Fact]
    public async Task Invitation_token_is_single_use()
    {
        var (owner, _, _) = await fx.RegisterAsync("inv");
        var email = $"once-{Guid.NewGuid():N}@example.com";
        var inv = await Json(await owner.PostAsJsonAsync("/api/v1/members/invitations", new { email, role = "ReadOnly" }));
        var token = inv.GetProperty("token").GetString();

        var first = await fx.NewClient().PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, fullName = "N", password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var second = await fx.NewClient().PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, fullName = "N", password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var bogus = await fx.NewClient().PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token = "nope", fullName = "N", password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
    }

    [Fact]
    public async Task User_in_two_organizations_can_switch_between_them()
    {
        var (a, orgA, emailA) = await fx.RegisterAsync("multi-a");
        var (b, orgB, _) = await fx.RegisterAsync("multi-b");

        var inv = await Json(await b.PostAsJsonAsync("/api/v1/members/invitations", new { email = emailA, role = "Finance" }));
        var accept = await a.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token = inv.GetProperty("token").GetString() });
        Assert.Equal(HttpStatusCode.NoContent, accept.StatusCode);

        var me = await Json(await a.GetAsync("/api/v1/me"));
        Assert.Equal(2, me.GetProperty("organizations").GetArrayLength());
        Assert.Equal("Finance", me.GetProperty("activeRole").GetString()); // accepting switches to the new org

        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsJsonAsync("/api/v1/me/active-organization", new { organizationId = orgA })).StatusCode);
        Assert.Equal("Owner", (await Json(await a.GetAsync("/api/v1/me"))).GetProperty("activeRole").GetString());
    }

    [Fact]
    public async Task Audit_log_records_sensitive_actions_and_is_append_only()
    {
        var (owner, orgId, _) = await fx.RegisterAsync("audit");
        await owner.PostAsJsonAsync("/api/v1/members/invitations", new { email = "aud@example.com", role = "ReadOnly" });

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var count = new NpgsqlCommand("""SELECT count(*) FROM "AuditLogs" WHERE "OrganizationId" = @o""", conn);
        count.Parameters.AddWithValue("o", orgId);
        Assert.True((long)(await count.ExecuteScalarAsync())! >= 3); // organization.created, membership.created, invitation.created

        await using var tamper = new NpgsqlCommand("""UPDATE "AuditLogs" SET "Action" = 'x' WHERE "OrganizationId" = @o""", conn);
        tamper.Parameters.AddWithValue("o", orgId);
        await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        await using var wipe = new NpgsqlCommand("""DELETE FROM "AuditLogs" WHERE "OrganizationId" = @o""", conn);
        wipe.Parameters.AddWithValue("o", orgId);
        await Assert.ThrowsAsync<PostgresException>(() => wipe.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Suspended_organization_rejects_writes_but_allows_reads()
    {
        var (owner, orgId, _) = await fx.RegisterAsync("susp");
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""UPDATE "Organizations" SET "Status" = 'Suspended' WHERE "Id" = @o""", conn);
            cmd.Parameters.AddWithValue("o", orgId);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await owner.PostAsJsonAsync("/api/v1/members/invitations", new { email = "s@example.com", role = "ReadOnly" })).StatusCode);
    }

    [Fact]
    public async Task Login_logout_and_bad_password()
    {
        var (_, _, email) = await fx.RegisterAsync("login");
        var c = fx.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "wrong-Password1!" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/v1/auth/login", new { email, password = ApiFixture.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/members")).StatusCode); // single org auto-selected
        await c.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me")).StatusCode);
    }
}
