using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Domain.Charges;
using Habita.Domain.Documents;
using Habita.Domain.Notifications;
using Habita.Domain.Organizations;
using Habita.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Documents;

/// <summary>A tenant's account on one page: what was charged, what was paid and what is left, as of today. Voided charges are left out.</summary>
public class StatementService(IAppDbContext db, ICurrentOrganization org, OrgCalendar calendar, IPdfConverter pdf)
{
    private static string StatusLabel(ChargeStatus s) => s switch
    {
        ChargeStatus.Paid => "Pagado", ChargeStatus.PartiallyPaid => "Pago parcial", ChargeStatus.Overdue => "Vencido", _ => "Por pagar",
    };

    public Task<Result<PdfFile>> PdfAsync(Guid tenantId, CancellationToken ct) => RenderAsync(tenantId, ct);

    public Task<Result<PdfFile>> PdfForTenantAsync(CancellationToken ct) =>
        org.Role == Role.Tenant && org.TenantId is { } tid ? RenderAsync(tid, ct) : Task.FromResult(new Result<PdfFile>(Outcome.Forbidden));

    private async Task<Result<PdfFile>> RenderAsync(Guid tenantId, CancellationToken ct)
    {
        var tenantName = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.FullName).FirstOrDefaultAsync(ct);
        if (tenantName is null) return new(Outcome.NotFound);
        var today = await calendar.TodayAsync(ct);
        var grace = await calendar.GraceDaysAsync(ct);

        var rows = await (from c in db.RentCharges.AsNoTracking()
                          join l in db.Leases on c.LeaseId equals l.Id
                          join u in db.Units on l.UnitId equals u.Id
                          join pr in db.Properties on u.PropertyId equals pr.Id
                          where l.TenantId == tenantId && c.Status != ChargeStatus.Voided
                          orderby c.DueDate, c.Id
                          select new { c, Place = u.Code + " · " + pr.Name,
                                       Allocated = db.PaymentAllocations.Where(a => a.RentChargeId == c.Id && a.ReversedAt == null).Sum(a => (decimal?)a.Amount) ?? 0m }).ToListAsync(ct);
        var charges = rows.Select(r =>
        {
            var paid = Math.Min(r.Allocated, r.c.Amount);
            return new StatementCharge(r.c.Description, r.Place, r.c.DueDate, r.c.Amount, paid, RentCharge.Balance(r.c.Amount, r.Allocated, voided: false),
                StatusLabel(RentCharge.Effective(r.c.Status, r.c.DueDate, today, grace)));
        }).ToList();

        var payments = await (from p in db.Payments.AsNoTracking() join r in db.Receipts on p.Id equals r.PaymentId where p.TenantId == tenantId
                              orderby p.ReceivedOn, r.Folio
                              select new { p.Id, p.ReceivedOn, p.Method, p.Amount, p.Status, r.Folio,
                                           Applied = db.PaymentAllocations.Where(a => a.PaymentId == p.Id && a.ReversedAt == null).Sum(a => (decimal?)a.Amount) ?? 0m }).ToListAsync(ct);
        var credit = payments.Where(p => p.Status == PaymentStatus.Confirmed).Sum(p => Payment.Unassigned(p.Amount, p.Applied));

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        var landlord = string.IsNullOrWhiteSpace(settings?.LandlordName) ? await db.Organizations.AsNoTracking().Where(o => o.Id == org.Id).Select(o => o.Name).FirstAsync(ct) : settings!.LandlordName.Trim();
        var data = new StatementData(landlord, PersonName.Display(tenantName), today, charges,
            payments.Select(p => new StatementPayment(Receipt.Format(p.Folio), p.ReceivedOn, ReceiptService.MethodLabel(p.Method), p.Amount, p.Status == PaymentStatus.Reversed)).ToList(),
            charges.Sum(c => c.Amount), charges.Sum(c => c.Paid), charges.Sum(c => c.Balance), credit);
        try { return new(Outcome.Ok, new PdfFile(await pdf.FromHtmlAsync(DocumentHtml.Statement(data), ct), $"Estado de cuenta {SpanishFormat.ShortDate(today).Replace('/', '-')}.pdf")); }
        catch (PdfUnavailableException e) { return new(Outcome.Unavailable, Error: e.Message); }
    }
}
