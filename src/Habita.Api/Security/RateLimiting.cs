using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Habita.Api.Security;

public static class RateLimiting
{
    public const string Auth = "auth";
    public const string Signup = "signup";
    public const string Upload = "upload";
    public const string Scan = "scan";
    public const string Push = "push";
    public const string Pdf = "pdf";

    public static bool Enabled(IConfiguration c) => c.GetValue("RateLimiting:Enabled", true);

    /// <summary>
    /// Per-client-IP limits on the endpoints an attacker would hammer: login, password reset, invitation tokens and sign-up.
    /// Account lockout (5 failed passwords) complements this per account.
    /// </summary>
    public static IServiceCollection AddHabitaRateLimiting(this IServiceCollection services, IConfiguration c)
    {
        if (!Enabled(c)) return services;
        var authPerMinute = c.GetValue("RateLimiting:AuthPerMinute", 10);
        var uploadsPerMinute = c.GetValue("RateLimiting:UploadsPerMinute", 20);
        var scansPerMinute = c.GetValue("RateLimiting:ScansPerMinute", 30);
        var pushPerMinute = c.GetValue("RateLimiting:PushPerMinute", 20);
        var pdfPerMinute = c.GetValue("RateLimiting:PdfPerMinute", 20);
        var signupPerWindow = c.GetValue("RateLimiting:SignupPerTenMinutes", 5);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(Auth, ctx => RateLimitPartition.GetFixedWindowLimiter(Client(ctx), _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(Signup, ctx => RateLimitPartition.GetFixedWindowLimiter(Client(ctx), _ => new FixedWindowRateLimiterOptions { PermitLimit = signupPerWindow, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
            o.AddPolicy(Upload, ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.Identity?.Name ?? Client(ctx), _ => new FixedWindowRateLimiterOptions { PermitLimit = uploadsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            // Resolving a cash code is a guessing target, so it is limited per signed-in person.
            o.AddPolicy(Scan, ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.Identity?.Name ?? Client(ctx), _ => new FixedWindowRateLimiterOptions { PermitLimit = scansPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(Push, ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.Identity?.Name ?? Client(ctx), _ => new FixedWindowRateLimiterOptions { PermitLimit = pushPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            // Every PDF is made by an external service that costs real CPU: limited per signed-in person.
            o.AddPolicy(Pdf, ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.Identity?.Name ?? Client(ctx), _ => new FixedWindowRateLimiterOptions { PermitLimit = pdfPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString();
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(new { title = "Demasiados intentos. Espera un momento e inténtalo de nuevo.", status = 429 }, ct);
            };
        });
        return services;
    }

    private static string Client(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
