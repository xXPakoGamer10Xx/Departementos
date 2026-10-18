using Habita.Application.Abstractions;
using Serilog.Context;

namespace Habita.Api.Security;

/// <summary>Baseline hardening headers on every response. The API only serves JSON/CSV, so the CSP forbids everything.</summary>
public class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            if (!h.ContainsKey("Cache-Control")) h["Cache-Control"] = "no-store"; // authenticated financial data must not be cached
            return Task.CompletedTask;
        });
        return next(ctx);
    }
}

/// <summary>
/// Defense in depth against cross-site request forgery on top of SameSite=Lax cookies: a state-changing request that
/// carries an Origin header must come from this API's own origin or from one of the configured web origins.
/// Requests without Origin (server-to-server, curl, tests) are not browser requests and pass.
/// </summary>
public class OriginCheckMiddleware(RequestDelegate next, IConfiguration config)
{
    private readonly HashSet<string> _allowed = ReadAllowed(config);

    public static HashSet<string> ReadAllowed(IConfiguration config)
    {
        var values = new List<string?>();
        if (config["Security:AllowedOrigins"] is { Length: > 0 } csv) values.AddRange(csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        values.AddRange(config.GetSection("Security:AllowedOrigins").GetChildren().Select(c => c.Value));
        return values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => Normalize(v!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string origin) => origin.Trim().TrimEnd('/');

    public async Task InvokeAsync(HttpContext ctx)
    {
        var method = ctx.Request.Method;
        var safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
        if (!safe && ctx.Request.Headers.Origin.ToString() is { Length: > 0 } origin)
        {
            var own = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            if (!_allowed.Contains(Normalize(origin)) && !string.Equals(Normalize(origin), own, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { title = "Origen no permitido", status = 403 });
                return;
            }
        }
        await next(ctx);
    }
}

/// <summary>JSON requests here are small. Reject oversized bodies up front and cap streaming bodies too.</summary>
public class RequestSizeLimitMiddleware(RequestDelegate next, IConfiguration config)
{
    private readonly long _jsonMax = config.GetValue("Security:MaxRequestBodyBytes", 1_000_000L);
    // File uploads (5 MB of content, see StoredFile.MaxBytes) get their own, larger cap on those routes only: payment proofs and the contract template.
    private readonly long _uploadMax = config.GetValue("Security:MaxUploadRequestBytes", 6_000_000L);

    public async Task InvokeAsync(HttpContext ctx)
    {
        var isUpload = HttpMethods.IsPost(ctx.Request.Method)
            && (ctx.Request.Path.Equals("/api/v1/files", StringComparison.OrdinalIgnoreCase) || ctx.Request.Path.Equals("/api/v1/contract-template", StringComparison.OrdinalIgnoreCase)
                || ctx.Request.Path.StartsWithSegments("/api/v1/imports", StringComparison.OrdinalIgnoreCase));
        var _max = isUpload ? _uploadMax : _jsonMax;
        if (ctx.Request.ContentLength > _max)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await ctx.Response.WriteAsJsonAsync(new { title = "La solicitud es demasiado grande", status = 413 });
            return;
        }
        if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = _max;
        await next(ctx);
    }
}

/// <summary>Adds organization and user ids (never names or emails) to every log line of the request.</summary>
public class LogContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, ICurrentOrganization org, ICurrentUser user)
    {
        using (LogContext.PushProperty("OrganizationId", org.Id))
        using (LogContext.PushProperty("UserId", user.UserId))
            await next(ctx);
    }
}
