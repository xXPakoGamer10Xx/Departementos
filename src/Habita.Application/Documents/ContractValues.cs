using System.Globalization;
using Habita.Domain.Charges;
using Habita.Domain.Documents;
using Habita.Domain.Leases;
using Habita.Domain.Notifications;
using Habita.Domain.Properties;
using Habita.Domain.Settings;
using Habita.Domain.Tenants;

namespace Habita.Application.Documents;

/// <summary>Turns a lease and the records around it into the text of every contract field. Every field of <see cref="ContractFields.All"/> always has a value (possibly empty).</summary>
public static class ContractValues
{
    public static Dictionary<string, string> Build(Lease lease, Tenant tenant, Unit unit, Property property, OrganizationSettings? settings, string currency, DateOnly today,
        IReadOnlyList<InventoryItem>? inventory = null, IReadOnlyList<RentCharge>? depositInstallments = null)
    {
        inventory ??= []; var installments = (depositInstallments ?? []).OrderBy(c => c.DueDate).ToList();
        var guarantor = (lease.GuarantorName ?? "").Trim(); var guarantorPhone = (lease.GuarantorPhone ?? "").Trim();
        var name = PersonName.Display(tenant.FullName);
        var landlord = (settings?.LandlordName ?? "").Trim();
        var phone = tenant.Phone ?? "";
        var deposit = lease.DepositAmount ?? 0m;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fecha_actual"] = SpanishFormat.Date(today),
            ["nombre_completo"] = name,
            ["tel_arrendatario"] = phone,
            ["email_arrendatario"] = tenant.Email ?? "",
            ["depto_numero"] = unit.Code,
            ["propiedad_nombre"] = property.Name,
            ["propiedad_direccion"] = property.Address,
            ["renta"] = SpanishFormat.Money(lease.MonthlyRent),
            ["renta_numero"] = lease.MonthlyRent.ToString("0.00", CultureInfo.InvariantCulture),
            ["renta_letra"] = MoneyInWords.ForCurrency(lease.MonthlyRent, currency),
            ["dia_pago"] = lease.DueDay.ToString(CultureInfo.InvariantCulture),
            ["fecha_pago"] = $"{lease.DueDay} de cada mes",
            ["fecha_inicio"] = SpanishFormat.Date(lease.StartDate),
            ["fecha_termino"] = lease.EndDate is { } end ? SpanishFormat.Date(end) : "tiempo indefinido",
            ["deposito"] = SpanishFormat.Money(deposit),
            ["deposito_numero"] = deposit.ToString("0.00", CultureInfo.InvariantCulture),
            ["deposito_letra"] = MoneyInWords.ForCurrency(deposit, currency),
            ["deposito_tipo"] = installments.Count switch { 0 => "", 1 => "en una sola exhibición", var n => $"en {n} abonos" },
            ["deposito_fechas"] = string.Join(", ", installments.Select(c => $"{SpanishFormat.Date(c.DueDate)} ({SpanishFormat.Money(c.Amount)})")),
            ["metodo_pago"] = "efectivo o transferencia",
            ["arrendador_nombre"] = landlord,
            ["arrendador_direccion"] = (settings?.LandlordAddress ?? "").Trim(),
            ["fiador_nombre"] = guarantor,
            ["fiador_telefono"] = guarantorPhone,
            ["inventario"] = string.Join("\n", inventory.OrderBy(i => i.SortOrder).Select(i => $"{i.Quantity} × {i.Name} — {InventoryItem.Label(i.Condition)}{(string.IsNullOrWhiteSpace(i.Notes) ? "" : $" ({i.Notes})")}")),
            ["observaciones"] = lease.TermsNotes ?? "",
            ["arrendatario_linea"] = $"ARRENDATARIO: {name}{(phone.Length > 0 ? $" — Tel: {phone}" : "")}",
            ["arrendador_linea"] = landlord.Length > 0 ? $"ARRENDADOR: {landlord}" : "",
            ["fiador_linea"] = guarantor.Length > 0 ? $"FIADOR: {guarantor}{(guarantorPhone.Length > 0 ? $" — Tel: {guarantorPhone}" : "")}" : "",
        };
        return values;
    }
}
