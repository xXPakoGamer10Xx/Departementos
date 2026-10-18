using Habita.Api.Auth;
using Habita.Application.Members;
using Habita.Application.Reports;
using Habita.Domain.Charges;
using Habita.Domain.Payments;

namespace Habita.Api.Endpoints;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this RouteGroupBuilder v1)
    {
        // The dashboard serves every role; the service withholds financial figures from roles that may not see them.
        v1.MapGet("/dashboard", async (ReportService svc, int? year, int? month, CancellationToken ct) => (await svc.DashboardAsync(year, month, ct)).ToResult())
            .RequireAuthorization(Policies.Staff);

        var reports = v1.MapGroup("/reports").RequireAuthorization(Policies.ReadReports); // Owner, Admin, Finance, ReadOnly (not the Collector)
        reports.MapGet("/financial-summary", async (ReportService svc, DateOnly from, DateOnly to, CancellationToken ct) => (await svc.SummaryAsync(from, to, ct)).ToResult());

        reports.MapGet("/charges.csv", async (HttpContext ctx, ReportService svc, DateOnly from, DateOnly to, ChargeStatus? status, CancellationToken ct) =>
            await Csv(ctx, await svc.ExportChargesAsync(from, to, status, ct), ct));
        reports.MapGet("/payments.csv", async (HttpContext ctx, ReportService svc, DateOnly from, DateOnly to, PaymentStatus? status, CancellationToken ct) =>
            await Csv(ctx, await svc.ExportPaymentsAsync(from, to, status, ct), ct));
        reports.MapGet("/balances.csv", async (HttpContext ctx, ReportService svc, CancellationToken ct) => await Csv(ctx, await svc.ExportBalancesAsync(ct), ct));
    }

    /// <summary>Streams rows as they are read, so a large export never sits in memory. Errors are decided before the first byte is written.</summary>
    private static async Task<IResult> Csv(HttpContext ctx, Result<ExportSet> result, CancellationToken ct)
    {
        if (result.Outcome != Outcome.Ok) return result.ToResult();
        var set = result.Value!;
        ctx.Response.ContentType = "text/csv; charset=utf-8";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{set.FileName}\"";
        ctx.Response.Headers.CacheControl = "no-store"; // financial data must not linger in shared caches
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await using var writer = new StreamWriter(ctx.Response.Body, Habita.Application.Reports.Csv.Utf8WithBom, leaveOpen: true);
        await writer.WriteAsync(Habita.Application.Reports.Csv.Row(set.Header));
        await foreach (var row in set.Rows.WithCancellation(ct)) await writer.WriteAsync(Habita.Application.Reports.Csv.Row(row));
        await writer.FlushAsync(ct);
        return Results.Empty;
    }
}
