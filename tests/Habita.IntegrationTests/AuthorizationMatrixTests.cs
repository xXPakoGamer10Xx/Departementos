using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Habita.IntegrationTests;

/// <summary>
/// The authorization matrix: every API route against every role, as a file kept in the repository (AuthorizationMatrix.txt, copied next to the tests).
/// "A" = the role gets past authorization (whatever the route then answers: 400, 404…), "-" = 403. Any change to who can reach what shows up as a failing
/// diff that someone has to read and approve by regenerating the file (run the tests with UPDATE_AUTH_MATRIX=1). Anonymous callers are checked separately
/// (Hito10Tests: every route answers 401 without a session).
/// </summary>
public class AuthorizationMatrixTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    static readonly string[] Roles = ["Owner", "Admin", "Finance", "Maintenance", "ReadOnly", "Collector", "Tenant"];

    static async Task<string> Row(HttpClient c, string method, string url)
    {
        // Headers only: the live notification stream never ends, so waiting for a body would wait forever.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var r = await c.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) }, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        return r.StatusCode == HttpStatusCode.Forbidden ? "-" : "A";
    }

    [Fact]
    public async Task Who_can_reach_what_matches_the_approved_matrix()
    {
        var (owner, _, _) = await fx.RegisterAsync("matrix");
        var clients = new Dictionary<string, HttpClient> { ["Owner"] = owner };
        foreach (var role in Roles.Where(r => r is not "Owner" and not "Tenant")) clients[role] = await fx.AddMemberAsync(owner, role);
        var tenantId = (await (await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = "Ana Matriz" })).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        var code = (await (await owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Tenant", tenantId })).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString();
        var tenant = fx.NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.PostAsJsonAsync("/api/v1/auth/access-codes/redeem", new { code, email = $"matriz-{Guid.NewGuid():N}@example.com", fullName = "Ana Matriz", password = ApiFixture.Password })).StatusCode);
        clients["Tenant"] = tenant;

        var endpoints = fx.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/v1") && !e.RoutePattern.RawText!.StartsWith("/api/v1/auth/")).ToList();
        var lines = new List<string>();
        foreach (var e in endpoints)
            foreach (var method in e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            {
                var raw = e.RoutePattern.RawText!.TrimEnd('/');
                var url = Regex.Replace(Regex.Replace(raw, @"\{[^}]*:guid[^}]*\}", _ => Guid.NewGuid().ToString()), @"\{[^}]+\}", "1");
                // Signing out ends the session of the client that asks; the matrix is about who may call, which is everyone signed in.
                if (url.EndsWith("/me/active-organization") || url.EndsWith("/logout")) { lines.Add($"{method,-6} {raw,-62} | " + string.Join(' ', Roles.Select(_ => "A"))); continue; }
                var marks = new List<string>();
                foreach (var role in Roles) marks.Add(await Row(clients[role], method, url));
                lines.Add($"{method,-6} {raw,-62} | {string.Join(' ', marks)}");
            }
        lines.Sort(StringComparer.Ordinal);
        var actual = $"# {string.Join(' ', Roles)}  (A = allowed through authorization, - = 403)\n" + string.Join('\n', lines) + "\n";

        var path = Path.Combine(AppContext.BaseDirectory, "AuthorizationMatrix.txt");
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "AuthorizationMatrix.txt"));
        if (Environment.GetEnvironmentVariable("UPDATE_AUTH_MATRIX") == "1") { await File.WriteAllTextAsync(source, actual, new UTF8Encoding(false)); return; }
        var expected = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n");
        if (expected == actual) return;
        var exp = expected.Split('\n').ToHashSet(); var act = actual.Split('\n').ToHashSet();
        var diff = string.Join('\n', exp.Except(act).Select(l => "  - " + l).Concat(act.Except(exp).Select(l => "  + " + l)));
        Assert.Fail($"The authorization matrix changed. If the change is intended, regenerate it with UPDATE_AUTH_MATRIX=1 and review the diff:\n{diff}");
    }
}
