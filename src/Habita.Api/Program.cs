using Habita.Api.Auth;
using Habita.Api.Endpoints;
using Habita.Api.Security;
using Habita.Application.Abstractions;
using Habita.Infrastructure;
using Habita.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);
var production = ProductionGuards.IsProduction(builder.Environment);
ProductionGuards.Enforce(builder.Configuration, builder.Environment);

builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false; // do not advertise the server software
    o.Limits.MaxRequestBodySize = builder.Configuration.GetValue("Security:MaxRequestBodyBytes", 1_000_000L);
});

// Structured logs: JSON in production, readable in development. Only ids are attached to requests; no names, emails or bodies.
builder.Host.UseSerilog((ctx, lc) =>
{
    lc.ReadFrom.Configuration(ctx.Configuration).Enrich.FromLogContext();
    if (production) lc.WriteTo.Console(new RenderedCompactJsonFormatter()); else lc.WriteTo.Console();
});

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<HttpCurrentUser>();
builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<HttpCurrentUser>());
builder.Services.AddHealthChecks().AddDbContextCheck<HabitaDbContext>();
builder.Services.AddHabitaRateLimiting(builder.Configuration);

// Cookie encryption keys must survive restarts and be shared by every API instance.
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    // Fail at boot, loudly, instead of returning 500s on the first login because the key folder is read-only.
    try { Directory.CreateDirectory(keysPath); var probe = Path.Combine(keysPath, $".write-test-{Guid.NewGuid():N}"); File.WriteAllText(probe, "ok"); File.Delete(probe); }
    catch (Exception e) { throw new InvalidOperationException($"DataProtection__KeysPath ({keysPath}) no es escribible por el usuario de la API: {e.Message}", e); }
    builder.Services.AddDataProtection().SetApplicationName("Habita").PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

// Behind the reverse proxy: trust X-Forwarded-* only from the configured private networks, so client IPs (rate limiting) are real.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    var cidrs = builder.Configuration["Security:TrustedProxyCidrs"] ?? (production ? "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,fc00::/7" : "");
    if (cidrs.Length > 0)
    {
        o.KnownIPNetworks.Clear(); o.KnownProxies.Clear();
        foreach (var cidr in cidrs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    }
});

var secureCookies = builder.Configuration.GetValue("Security:SecureCookies", production);
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies(o => o.ApplicationCookie!.Configure(c =>
    {
        c.Cookie.Name = "habita.auth";
        c.Cookie.HttpOnly = true;                  // not readable by scripts
        c.Cookie.SameSite = SameSiteMode.Lax;      // not sent on cross-site POSTs
        c.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        c.ExpireTimeSpan = TimeSpan.FromHours(12);
        c.SlidingExpiration = true;
        c.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        c.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    }));
builder.Services.AddAuthorization(o => o.AddHabitaPolicies());
builder.Services.AddScoped<IAuthorizationHandler, RoleHandler>();

var app = builder.Build();

// One-off commands for operations: `dotnet Habita.Api.dll --migrate` / `--seed-demo` run and exit.
if (await Habita.Api.Operations.Commands.RunAsync(app, args)) return;

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<HabitaDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseSerilogRequestLogging();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();
// TLS is terminated at the edge (Caddy in the production compose). The API redirects only if it is itself exposed over HTTPS.
if (app.Configuration.GetValue<bool>("Security:HttpsRedirection")) { app.UseHsts(); app.UseHttpsRedirection(); }
app.UseMiddleware<OriginCheckMiddleware>();
app.UseMiddleware<RequestSizeLimitMiddleware>();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseRouting();
if (RateLimiting.Enabled(app.Configuration)) app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<OrganizationContextMiddleware>();
app.UseMiddleware<LogContextMiddleware>();
app.UseAuthorization();

app.MapHealthChecks("/health");
var v1 = app.MapGroup("/api/v1");
v1.MapAuthEndpoints();
v1.MapMeEndpoints();
v1.MapMemberEndpoints();
v1.MapPropertyEndpoints();
v1.MapTenantEndpoints();
v1.MapLeaseEndpoints();
v1.MapChargeEndpoints();
v1.MapPaymentEndpoints();
v1.MapMaintenanceEndpoints();
v1.MapReportEndpoints();
v1.MapAuditEndpoints();
v1.MapSettingsEndpoints();
v1.MapAccessCodeEndpoints();
v1.MapFileEndpoints();
v1.MapPortalEndpoints();
v1.MapCollectionEndpoints();
v1.MapNotificationEndpoints();
v1.MapDocumentEndpoints();
v1.MapLeaseExtrasEndpoints();
v1.MapImportEndpoints();

app.Run();

public partial class Program;
