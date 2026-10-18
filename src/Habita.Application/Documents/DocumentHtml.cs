using System.Net;
using System.Text;
using Habita.Domain.Documents;
using Habita.Domain.Notifications;

namespace Habita.Application.Documents;

public record ReceiptLine(string Description, string Place, decimal Amount);

/// <summary>
/// Everything a receipt shows, and nothing else: no bank account, no staff notes, no reason for a reversal, no internal ids. A tenant can be handed
/// this document as it is.
/// </summary>
public record ReceiptData(string Folio, bool Cancelled, DateOnly? CancelledOn, string LandlordName, string LandlordAddress, string TenantName, DateOnly ReceivedOn,
    decimal Amount, string AmountInWords, string MethodLabel, string? Reference, IReadOnlyList<ReceiptLine> Lines, decimal Unassigned, DateOnly IssuedOn);

public record StatementCharge(string Description, string Place, DateOnly DueDate, decimal Amount, decimal Paid, decimal Balance, string StatusLabel);
public record StatementPayment(string Folio, DateOnly ReceivedOn, string MethodLabel, decimal Amount, bool Reversed);
public record StatementData(string LandlordName, string TenantName, DateOnly AsOf, IReadOnlyList<StatementCharge> Charges, IReadOnlyList<StatementPayment> Payments,
    decimal TotalCharged, decimal TotalPaid, decimal Balance, decimal Credit);

/// <summary>Printable pages as HTML for the PDF service. Every value is HTML-encoded and there are no scripts and no outside resources.</summary>
public static class DocumentHtml
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Style = """
        @page { size: A4; margin: 16mm; }
        * { box-sizing: border-box; }
        body { font-family: "Liberation Sans", Arial, Helvetica, sans-serif; font-size: 11pt; color: #1b2430; line-height: 1.4; }
        h1 { font-size: 16pt; margin: 0; letter-spacing: .04em; }
        h2 { font-size: 11pt; margin: 18pt 0 6pt; text-transform: uppercase; letter-spacing: .06em; color: #4a5565; }
        .top { display: flex; justify-content: space-between; align-items: flex-start; gap: 24pt; border-bottom: 2px solid #1b2430; padding-bottom: 10pt; }
        .muted { color: #5b6676; font-size: 10pt; }
        .folio { text-align: right; }
        .folio strong { display: block; font-size: 14pt; font-family: "Liberation Mono", monospace; }
        dl { display: grid; grid-template-columns: 34mm 1fr; gap: 4pt 10pt; margin: 10pt 0; }
        dt { color: #5b6676; } dd { margin: 0; font-weight: 600; }
        .amount { border: 1.5px solid #1b2430; padding: 10pt 12pt; margin: 12pt 0; }
        .amount .n { font-size: 20pt; font-weight: 700; font-family: "Liberation Mono", monospace; }
        table { width: 100%; border-collapse: collapse; margin-top: 4pt; }
        th, td { text-align: left; padding: 5pt 6pt; border-bottom: 1px solid #d5dae1; vertical-align: top; }
        th { font-size: 9.5pt; text-transform: uppercase; letter-spacing: .05em; color: #4a5565; border-bottom: 1.5px solid #1b2430; }
        td.r, th.r { text-align: right; font-variant-numeric: tabular-nums; }
        tfoot td { font-weight: 700; border-bottom: 0; }
        .stamp { display: inline-block; border: 3px solid #b3261e; color: #b3261e; font-size: 22pt; font-weight: 800; letter-spacing: .12em; padding: 2pt 14pt; transform: rotate(-6deg); margin: 10pt 0; }
        .void td { color: #8a93a1; text-decoration: line-through; }
        .note { margin-top: 18pt; font-size: 9.5pt; color: #5b6676; border-top: 1px solid #d5dae1; padding-top: 8pt; }
        .totals { margin-top: 14pt; display: grid; grid-template-columns: repeat(4, 1fr); gap: 8pt; }
        .totals div { border: 1px solid #d5dae1; padding: 8pt; } .totals span { display: block; font-size: 9pt; color: #5b6676; text-transform: uppercase; }
        .totals b { font-size: 13pt; font-family: "Liberation Mono", monospace; }
        """;

    private static void Head(StringBuilder sb, string title) =>
        sb.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>").Append(E(title)).Append("</title><style>").Append(Style).Append("</style></head><body>");

    public static string Receipt(ReceiptData r)
    {
        var sb = new StringBuilder();
        Head(sb, $"Recibo {r.Folio}");
        sb.Append("<div class=\"top\"><div><h1>RECIBO DE PAGO</h1>");
        if (r.LandlordName.Length > 0) sb.Append("<div><strong>").Append(E(r.LandlordName)).Append("</strong></div>");
        if (r.LandlordAddress.Length > 0) sb.Append("<div class=\"muted\">").Append(E(r.LandlordAddress)).Append("</div>");
        sb.Append("</div><div class=\"folio\"><span class=\"muted\">Folio</span><strong>").Append(E(r.Folio)).Append("</strong><span class=\"muted\">Emitido el ").Append(E(SpanishFormat.Date(r.IssuedOn))).Append("</span></div></div>");

        if (r.Cancelled)
            sb.Append("<div class=\"stamp\">CANCELADO</div><p class=\"muted\">Este recibo se canceló")
              .Append(r.CancelledOn is { } c ? $" el {E(SpanishFormat.Date(c))}" : "").Append(" porque el pago se revirtió. No sirve como comprobante de pago.</p>");

        sb.Append("<dl><dt>Recibí de</dt><dd>").Append(E(r.TenantName)).Append("</dd>");
        sb.Append("<dt>Fecha del pago</dt><dd>").Append(E(SpanishFormat.Date(r.ReceivedOn))).Append("</dd>");
        sb.Append("<dt>Forma de pago</dt><dd>").Append(E(r.MethodLabel)).Append("</dd>");
        if (!string.IsNullOrWhiteSpace(r.Reference)) sb.Append("<dt>Referencia</dt><dd>").Append(E(r.Reference)).Append("</dd>");
        sb.Append("</dl>");

        sb.Append("<div class=\"amount").Append(r.Cancelled ? " void" : "").Append("\"><div class=\"muted\">La cantidad de</div><div class=\"n\">").Append(E(SpanishFormat.Money(r.Amount)))
          .Append("</div><div>(").Append(E(r.AmountInWords)).Append(")</div></div>");

        if (r.Lines.Count > 0)
        {
            sb.Append("<h2>Se aplicó a</h2><table><thead><tr><th>Concepto</th><th>Inmueble</th><th class=\"r\">Importe</th></tr></thead><tbody>");
            foreach (var l in r.Lines)
                sb.Append("<tr").Append(r.Cancelled ? " class=\"void\"" : "").Append("><td>").Append(E(l.Description)).Append("</td><td>").Append(E(l.Place)).Append("</td><td class=\"r\">")
                  .Append(E(SpanishFormat.Money(l.Amount))).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        if (!r.Cancelled && r.Unassigned > 0)
            sb.Append("<p><strong>Saldo a favor de este pago:</strong> ").Append(E(SpanishFormat.Money(r.Unassigned))).Append(". Se aplicará a cargos futuros.</p>");

        sb.Append("<p class=\"note\">Recibo interno de pago. No es una factura (CFDI) ni sustituye a un comprobante fiscal.</p></body></html>");
        return sb.ToString();
    }

    public static string Statement(StatementData s)
    {
        var sb = new StringBuilder();
        Head(sb, "Estado de cuenta");
        sb.Append("<div class=\"top\"><div><h1>ESTADO DE CUENTA</h1><div><strong>").Append(E(s.TenantName)).Append("</strong></div>");
        if (s.LandlordName.Length > 0) sb.Append("<div class=\"muted\">").Append(E(s.LandlordName)).Append("</div>");
        sb.Append("</div><div class=\"folio\"><span class=\"muted\">Al</span><strong>").Append(E(SpanishFormat.ShortDate(s.AsOf))).Append("</strong></div></div>");

        sb.Append("<div class=\"totals\"><div><span>Cargado</span><b>").Append(E(SpanishFormat.Money(s.TotalCharged))).Append("</b></div><div><span>Pagado</span><b>")
          .Append(E(SpanishFormat.Money(s.TotalPaid))).Append("</b></div><div><span>Saldo por pagar</span><b>").Append(E(SpanishFormat.Money(s.Balance)))
          .Append("</b></div><div><span>Saldo a favor</span><b>").Append(E(SpanishFormat.Money(s.Credit))).Append("</b></div></div>");

        sb.Append("<h2>Cargos</h2>");
        if (s.Charges.Count == 0) sb.Append("<p class=\"muted\">No hay cargos.</p>");
        else
        {
            sb.Append("<table><thead><tr><th>Vence</th><th>Concepto</th><th>Inmueble</th><th class=\"r\">Importe</th><th class=\"r\">Pagado</th><th class=\"r\">Saldo</th><th>Estado</th></tr></thead><tbody>");
            foreach (var c in s.Charges)
                sb.Append("<tr><td>").Append(E(SpanishFormat.ShortDate(c.DueDate))).Append("</td><td>").Append(E(c.Description)).Append("</td><td>").Append(E(c.Place)).Append("</td><td class=\"r\">")
                  .Append(E(SpanishFormat.Money(c.Amount))).Append("</td><td class=\"r\">").Append(E(SpanishFormat.Money(c.Paid))).Append("</td><td class=\"r\">").Append(E(SpanishFormat.Money(c.Balance)))
                  .Append("</td><td>").Append(E(c.StatusLabel)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        sb.Append("<h2>Pagos</h2>");
        if (s.Payments.Count == 0) sb.Append("<p class=\"muted\">No hay pagos.</p>");
        else
        {
            sb.Append("<table><thead><tr><th>Fecha</th><th>Recibo</th><th>Forma de pago</th><th class=\"r\">Importe</th></tr></thead><tbody>");
            foreach (var p in s.Payments)
                sb.Append("<tr").Append(p.Reversed ? " class=\"void\"" : "").Append("><td>").Append(E(SpanishFormat.ShortDate(p.ReceivedOn))).Append("</td><td>").Append(E(p.Folio)).Append(p.Reversed ? " (cancelado)" : "")
                  .Append("</td><td>").Append(E(p.MethodLabel)).Append("</td><td class=\"r\">").Append(E(SpanishFormat.Money(p.Amount))).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("<p class=\"note\">Estado de cuenta informativo a la fecha indicada. Los pagos cancelados no se suman. No es una factura (CFDI).</p></body></html>");
        return sb.ToString();
    }
}
