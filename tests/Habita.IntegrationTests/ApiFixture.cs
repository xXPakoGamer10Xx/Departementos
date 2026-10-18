using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace Habita.IntegrationTests;

public class ApiFixture : IAsyncLifetime
{
    public const string Password = "Passw0rd!Test";
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16").Build();
    public WebApplicationFactory<Program> Factory { get; protected set; } = null!;
    private readonly string _files = Path.Combine(Path.GetTempPath(), "habita-test-files-" + Guid.NewGuid().ToString("N"));

    public virtual async Task InitializeAsync()
    {
        await _db.StartAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Default", _db.GetConnectionString());
            b.UseSetting("Database:MigrateOnStartup", "true");
            b.UseSetting("Auth:ExposeInvitationTokens", "true");
            b.UseSetting("Storage:Root", _files);
            b.UseSetting("RateLimiting:Enabled", "false"); // every test calls from one IP; the limiter has its own dedicated tests
        });
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _db.DisposeAsync();
        if (Directory.Exists(_files)) Directory.Delete(_files, recursive: true);
    }

    public string ConnectionString => _db.GetConnectionString();
    public HttpClient NewClient() => Factory.CreateClient(); // keeps its own cookie jar

    /// <summary>Registers a new user + organization and returns a client already signed in.</summary>
    public async Task<(HttpClient Client, Guid OrgId, string Email)> RegisterAsync(string? tag = null)
    {
        var client = NewClient();
        var email = $"{(tag ?? "u")}-{Guid.NewGuid():N}@example.com";
        var r = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = Password, fullName = "Usuario Prueba", organizationName = $"Org {tag} {Guid.NewGuid():N}" });
        r.EnsureSuccessStatusCode();
        var body = await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return (client, body.GetProperty("organizationId").GetGuid(), email);
    }

    /// <summary>Invites an email with a role from an Owner/Admin client and returns a client signed in as the new member.</summary>
    public async Task<HttpClient> AddMemberAsync(HttpClient inviter, string role)
    {
        var email = $"{role.ToLower()}-{Guid.NewGuid():N}@example.com";
        var inv = await inviter.PostAsJsonAsync("/api/v1/members/invitations", new { email, role });
        inv.EnsureSuccessStatusCode();
        var token = (await inv.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetString();
        var member = NewClient();
        var acc = await member.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, fullName = role, password = Password });
        acc.EnsureSuccessStatusCode();
        return member;
    }
}

/// <summary>An API with the demo data already seeded, once per test class (xUnit builds a new test-class instance per test).</summary>
public class SeededApiFixture : ApiFixture
{
    public string Report { get; private set; } = "";
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Report = await Habita.Api.Operations.DemoSeeder.RunAsync(Factory.Services);
    }
}
