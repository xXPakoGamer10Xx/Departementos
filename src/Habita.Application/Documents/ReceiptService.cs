using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Domain.Documents;
using Habita.Domain.Organizations;
using Habita.Domain.Notifications;
using Habita.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Documents;

public record PdfFile(byte[] Bytes, string FileName);
public record ReceiptInfo(Guid PaymentId, int Folio, string FolioText, DateTimeOffset IssuedAt, bool Cancelled);

/// <summary>
/// Receipts. The number (folio) is given when the payment is recorded and never changes; the page itself is drawn from the payment each time it is asked
/// for, so a payment that is reversed later shows CANCELADO on its next printout. A receipt is internal proof of payment, not an invoice.
/// </summary>
public class ReceiptService(IAppDbContext db, ICurrentOrganization org, OrgCalendar calendar, IPdfConverter pdf)
{
    public static string MethodLabel(PaymentMethod m) => m switch
    {
        PaymentMethod.Cash => "Efectivo", PaymentMethod.BankTransfer => "Transferencia", PaymentMethod.Card => "Tarjeta", _ => "Otro",
    };

    public async Task<Result<ReceiptInfo>> InfoAsync(Guid paymentId, CancellationToken ct)
    {
        var row = await (from p in db.Payments.AsNoTracking() join r in db.Receipts on p.Id equals r.PaymentId where p.Id == paymentId select new { r.Folio, r.IssuedAt, p.Status }).FirstOrDefaultAsync(ct);
        return row is null ? new(Outcome.NotFound)
            : new(Outcome.Ok, new ReceiptInfo(paymentId, row.Folio, Receipt.Format(row.Folio), row.IssuedAt, row.Status == PaymentStatus.Reversed));
    }

    /// <summary>For staff (the endpoint's policy decides who).</summary>
    public Task<Result<PdfFile>> PdfAsync(Guid paymentId, CancellationToken ct) => RenderAsync(paymentId, tenantOnly: false, ct);

    /// <summary>For a tenant login: only their own payments. Another tenant's payment looks like a missing one.</summary>
    public Task<Result<PdfFile>> PdfForTenantAsync(Guid paymentId, CancellationToken ct) => RenderAsync(paymentId, tenantOnly: true, ct);

    private async Task<Result<PdfFile>> RenderAsync(Guid paymentId, bool tenantOnly, CancellationToken ct)
    {
        Guid? mine = null;
        if (tenantOnly)
        {
            if (org.Role != Role.Tenant || org.TenantId is not { } tid) return new(Outcome.Forbidden);
            mine = tid;
        }
        var row = await (from p in db.Payments.AsNoTracking()
                         join t in db.Tenants on p.TenantId equals t.Id
                         join r in db.Receipts on p.Id equals r.PaymentId
                         where p.Id == paymentId && (mine == null || p.TenantId == mine)
                         select new { p, TenantName = t.FullName, r.Folio, r.IssuedAt }).FirstOrDefaultAsync(ct);
        if (row is null) return new(Outcome.NotFound);

        var lines = await (from a in db.PaymentAllocations.AsNoTracking()
                           join c in db.RentCharges on a.RentChargeId equals c.Id
                           join l in db.Leases on c.LeaseId equals l.Id
                           join u in db.Units on l.UnitId equals u.Id
                           join pr in db.Properties on u.PropertyId equals pr.Id
                           where a.PaymentId == paymentId orderby a.CreatedAt, c.DueDate
                           select new ReceiptLine(c.Description, u.Code + " · " + pr.Name, a.Amount)).ToListAsync(ct);
        var applied = await db.PaymentAllocations.AsNoTracking().Where(a => a.PaymentId == paymentId && a.ReversedAt == null).SumAsync(a => (decimal?)a.Amount, ct) ?? 0m;

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        var organization = await db.Organizations.AsNoTracking().Where(o => o.Id == org.Id).Select(o => new { o.Name, o.Currency }).FirstAsync(ct);
        var landlord = string.IsNullOrWhiteSpace(settings?.LandlordName) ? organization.Name : settings!.LandlordName.Trim();
        var reversed = row.p.Status == PaymentStatus.Reversed;

        var data = new ReceiptData(Receipt.Format(row.Folio), reversed, reversed && row.p.ReversedAt is { } at ? await calendar.LocalDateOfAsync(at, ct) : null,
            landlord, (settings?.LandlordAddress ?? "").Trim(), PersonName.Display(row.TenantName), row.p.ReceivedOn, row.p.Amount,
            MoneyInWords.ForCurrency(row.p.Amount, organization.Currency), MethodLabel(row.p.Method), row.p.Reference, lines,
            reversed ? 0m : Payment.Unassigned(row.p.Amount, applied), await calendar.LocalDateOfAsync(row.IssuedAt, ct));
        try { return new(Outcome.Ok, new PdfFile(await pdf.FromHtmlAsync(DocumentHtml.Receipt(data), ct), $"Recibo {data.Folio}.pdf")); }
        catch (PdfUnavailableException e) { return new(Outcome.Unavailable, Error: e.Message); }
    }
}
