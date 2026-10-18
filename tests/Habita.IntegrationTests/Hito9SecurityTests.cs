using System.Net;
using System.Net.Http.Json;
using System.Text;
using Habita.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Habita.IntegrationTests;

public class Hito9SecurityTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    HttpClient Client(Action<IWebHostBuilder> configure, bool https = false, bool handleCookies = true) =>
        fx.Factory.WithWebHostBuilder(configure).CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(https ? "https://localhost" : "http://localhost"), HandleCookies = handleCookies });

    static Task<HttpResponseMessage> Login(HttpClient c, string email, string password) => c.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

    [Fact]
    public async Task Every_response_carries_the_hardening_headers_and_is_not_cacheable()
    {
        var (owner, _, _) = await fx.RegisterAsync("hdr");
        foreach (var url in new[] { "/health", "/api/v1/me", "/api/v1/properties" })
        {
            var r = await owner.GetAsync(url);
            Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
            Assert.Contains("frame-ancestors 'none'", r.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Contains("camera=()", r.Headers.GetValues("Permissions-Policy").Single());
            Assert.Equal("same-origin", r.Headers.GetValues("Cross-Origin-Opener-Policy").Single());
            Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
            Assert.False(r.Headers.Contains("Server"));
            Assert.False(r.Headers.Contains("X-Powered-By"));
        }
        Assert.True((await fx.NewClient().GetAsync("/api/v1/me")).Headers.Contains("X-Content-Type-Options")); // also on errors (401)
    }

    [Fact]
    public async Task State_changing_requests_from_a_foreign_origin_are_rejected_but_known_origins_and_non_browser_calls_pass()
    {
        var evil = Client(b => b.UseSetting("Security:AllowedOrigins", "https://app.habita.example,https://www.habita.example"));
        async Task<HttpStatusCode> Post(HttpClient c, string? origin)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { email = "nadie@example.com", password = "x" }) };
            if (origin is not null) req.Headers.Add("Origin", origin);
            return (await c.SendAsync(req)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.Forbidden, await Post(evil, "https://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(evil, "http://app.habita.example"));         // same host, wrong scheme
        Assert.Equal(HttpStatusCode.Unauthorized, await Post(evil, "https://app.habita.example"));     // allowed → reaches the login (bad credentials)
        Assert.Equal(HttpStatusCode.Unauthorized, await Post(evil, "https://www.habita.example/"));    // trailing slash tolerated
        Assert.Equal(HttpStatusCode.Unauthorized, await Post(evil, null));                              // no Origin: not a browser request
        Assert.Equal(HttpStatusCode.Unauthorized, await Post(evil, "http://localhost"));               // its own origin
        Assert.Equal(HttpStatusCode.OK, (await evil.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/health") { Headers = { { "Origin", "https://evil.example" } } })).StatusCode); // reads are unaffected
    }

    [Fact]
    public async Task Login_is_rate_limited_per_client_with_retry_after_while_other_endpoints_are_untouched()
    {
        var c = Client(b => { b.UseSetting("RateLimiting:Enabled", "true"); b.UseSetting("RateLimiting:AuthPerMinute", "3"); });
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "nadie@example.com", "mala-Clave1!")).StatusCode);
        var blocked = await Login(c, "nadie@example.com", "mala-Clave1!");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(int.Parse(blocked.Headers.GetValues("Retry-After").Single()) is > 0 and <= 60);
        Assert.Contains("Demasiados intentos", await blocked.Content.ReadAsStringAsync());

        // Reset and invitation endpoints share that budget; reading data and health checks never count.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsJsonAsync("/api/v1/auth/reset-password", new { email = "a@b.com", token = "x", newPassword = "Passw0rd!Test" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Sign_up_and_password_recovery_have_a_stricter_limit()
    {
        var c = Client(b => { b.UseSetting("RateLimiting:Enabled", "true"); b.UseSetting("RateLimiting:SignupPerTenMinutes", "2"); });
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/v1/auth/register",
                new { email = $"rl-{Guid.NewGuid():N}@example.com", password = ApiFixture.Password, fullName = "Prueba", organizationName = $"Org {Guid.NewGuid():N}" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsJsonAsync("/api/v1/auth/register",
            new { email = $"rl-{Guid.NewGuid():N}@example.com", password = ApiFixture.Password, fullName = "Prueba", organizationName = "Otra" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = "x@example.com" })).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_password()
    {
        var (_, _, email) = await fx.RegisterAsync("lock");
        var c = fx.NewClient();
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, email, "incorrecta-Clave1!")).StatusCode);
        Assert.Equal((HttpStatusCode)423, (await Login(c, email, "incorrecta-Clave1!")).StatusCode); // the 5th failure locks the account
        Assert.Equal((HttpStatusCode)423, (await Login(c, email, ApiFixture.Password)).StatusCode); // the correct one is refused while locked
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "nadie-" + Guid.NewGuid().ToString("N") + "@example.com", "x")).StatusCode); // unknown accounts look the same as wrong passwords
    }

    [Fact]
    public async Task The_session_cookie_is_httponly_samesite_lax_and_secure_when_configured()
    {
        var (_, _, email) = await fx.RegisterAsync("cookie");
        var plain = await Login(fx.NewClient(), email, ApiFixture.Password);
        var plainCookie = plain.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("habita.auth")).ToLowerInvariant();
        Assert.Contains("httponly", plainCookie);
        Assert.Contains("samesite=lax", plainCookie);

        var secure = await Login(Client(b => b.UseSetting("Security:SecureCookies", "true"), https: true), email, ApiFixture.Password);
        var secureCookie = secure.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("habita.auth")).ToLowerInvariant();
        Assert.Contains("secure", secureCookie);
        Assert.Contains("httponly", secureCookie);
    }

    [Fact]
    public async Task Oversized_bodies_are_refused_before_being_read()
    {
        var (owner, _, _) = await fx.RegisterAsync("big");
        var tooBig = new StringContent(new string('x', 1_100_000), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await owner.PostAsync("/api/v1/properties", tooBig)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/properties", new { name = "Normal", address = "x" })).StatusCode);
    }
}

public class ProductionGuardsTests
{
    sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Habita";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    static IConfiguration Config(params (string, string?)[] values) => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

    static readonly (string, string?)[] Good =
    [
        ("ConnectionStrings:Default", "Host=db;Database=habita;Username=habita;Password=Zr7!hQ2vL9xTq4Nw"), ("Web:BaseUrl", "https://habita.example"),
        ("Security:AllowedOrigins", "https://habita.example"), ("AllowedHosts", "habita.example"), ("Smtp:Host", "smtp.example"), ("DataProtection:KeysPath", "/keys"), ("Pdf:GotenbergUrl", "http://gotenberg:3000"),
    ];

    [Fact] public void A_complete_production_configuration_has_no_problems() => Assert.Empty(ProductionGuards.Problems(Config(Good), new Env("Production")));

    [Fact]
    public void Development_and_Testing_are_never_blocked() =>
        Assert.All(new[] { "Development", "Testing" }, e => Assert.Empty(ProductionGuards.Problems(Config(), new Env(e))));

    [Fact]
    public void An_empty_production_configuration_reports_every_gap()
    {
        var problems = ProductionGuards.Problems(Config(), new Env("Production"));
        Assert.Contains(problems, p => p.Contains("ConnectionStrings__Default"));
        Assert.Contains(problems, p => p.Contains("Web__BaseUrl"));
        Assert.Contains(problems, p => p.Contains("AllowedOrigins"));
        Assert.Contains(problems, p => p.Contains("AllowedHosts"));
        Assert.Contains(problems, p => p.Contains("Smtp__Host"));
        Assert.Contains(problems, p => p.Contains("DataProtection__KeysPath"));
        Assert.Contains(problems, p => p.Contains("Pdf__GotenbergUrl"));
        Assert.Throws<InvalidOperationException>(() => ProductionGuards.Enforce(Config(), new Env("Production")));
    }

    [Theory]
    [InlineData("ConnectionStrings:Default", "Host=db;Database=habita;Username=habita;Password=change-me", "contraseña de ejemplo")]
    [InlineData("Web:BaseUrl", "http://habita.example", "https://")]
    [InlineData("Security:AllowedOrigins", "http://habita.example", "https://")]
    [InlineData("AllowedHosts", "*", "AllowedHosts")]
    [InlineData("Auth:ExposeInvitationTokens", "true", "ExposeInvitationTokens")]
    [InlineData("RateLimiting:Enabled", "false", "RateLimiting")]
    [InlineData("Security:SecureCookies", "false", "SecureCookies")]
    public void Each_insecure_setting_is_caught(string key, string value, string expected)
    {
        var config = Good.Where(g => g.Item1 != key).Append((key, value)).ToArray();
        Assert.Contains(ProductionGuards.Problems(Config(config), new Env("Production")), p => p.Contains(expected));
    }

    // Device notifications (Web Push) are optional, but a half-set configuration is a mistake worth stopping at boot.
    [Fact]
    public void Push_keys_are_both_set_or_both_empty()
    {
        string[] oneKey = ["Push:VapidPublicKey", "Push:VapidPrivateKey"];
        foreach (var only in oneKey)
        {
            var problems = ProductionGuards.Problems(Config([.. Good, (only, "abc"), ("Push:Subject", "mailto:admin@habita.example")]), new Env("Production"));
            Assert.Contains(problems, p => p.Contains("Push__VapidPublicKey y Push__VapidPrivateKey"));
        }
        // The production compose file passes empty strings when push is not wanted: that is "off", not an error.
        Assert.Empty(ProductionGuards.Problems(Config([.. Good, ("Push:VapidPublicKey", ""), ("Push:VapidPrivateKey", ""), ("Push:Subject", "mailto:no-reply@habita.example")]), new Env("Production")));
        Assert.Empty(ProductionGuards.Problems(Config(Good), new Env("Production")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin@habita.example")]
    [InlineData("http://habita.example")]
    public void Push_keys_need_a_contact_to_identify_the_sender(string? subject)
    {
        var config = new List<(string, string?)>(Good) { ("Push:VapidPublicKey", "pub"), ("Push:VapidPrivateKey", "priv") };
        if (subject is not null) config.Add(("Push:Subject", subject));
        Assert.Contains(ProductionGuards.Problems(Config([.. config]), new Env("Production")), p => p.Contains("Push__Subject"));
    }

    [Theory]
    [InlineData("mailto:admin@habita.example")]
    [InlineData("https://habita.example/contact")]
    public void Push_keys_with_a_valid_contact_are_accepted(string subject) =>
        Assert.Empty(ProductionGuards.Problems(Config([.. Good, ("Push:VapidPublicKey", "pub"), ("Push:VapidPrivateKey", "priv"), ("Push:Subject", subject)]), new Env("Production")));
}
