using Habita.Domain.Documents;
using Habita.Domain.Notifications;
using Habita.Domain.Payments;

namespace Habita.Application.Notifications;

public record NotificationMessage(NotificationType Type, string Title, string Body, string Url);

/// <summary>
/// The words of every notification. A message about a person always opens with that person's name, first name first and then the surnames
/// (see <see cref="PersonName"/>): "Ana Ejemplo reportó un pago", "Ana Ejemplo, tu renta vence hoy". Nothing here reorders a name.
/// </summary>
public static class NotificationText
{
    public static string Money(decimal amount) => SpanishFormat.Money(amount);
    public static string Date(DateOnly d) => SpanishFormat.Date(d);
    public static string Days(int n) => SpanishFormat.Days(n);
    private static string MethodLabel(PaymentMethod m) => m switch { PaymentMethod.BankTransfer => "transferencia", PaymentMethod.Cash => "efectivo", PaymentMethod.Card => "tarjeta", _ => "otro método" };
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---------- events: staff ----------

    public static NotificationMessage ClaimSubmitted(string tenant, decimal amount, PaymentMethod method, DateOnly paidOn) =>
        new(NotificationType.ClaimSubmitted, $"{PersonName.Display(tenant)} reportó un pago",
            $"{Money(amount)} por {MethodLabel(method)}, pagado el {Date(paidOn)}. Revisa el comprobante y confírmalo o recházalo.", "/claims");

    public static NotificationMessage IssueReported(string tenant, string issueTitle, string? unitCode, Guid issueId) =>
        new(NotificationType.IssueReported, $"{PersonName.Display(tenant)} reportó una falla",
            Clip(unitCode is null ? issueTitle : $"{issueTitle} · unidad {unitCode}", 500), $"/maintenance/{issueId}");

    // ---------- events: tenant ----------

    public static NotificationMessage ClaimConfirmed(string tenant, decimal amount, DateOnly paidOn) =>
        new(NotificationType.ClaimConfirmed, $"{PersonName.Display(tenant)}, tu pago de {Money(amount)} fue confirmado",
            $"La administración registró tu pago del {Date(paidOn)}. Ya aparece en tu estado de cuenta.", "/portal/payments");

    public static NotificationMessage ClaimRejected(string tenant, decimal amount, string comment) =>
        new(NotificationType.ClaimRejected, $"{PersonName.Display(tenant)}, tu reporte de pago de {Money(amount)} no fue aceptado",
            Clip(comment, 500), "/portal/pay");

    public static NotificationMessage CashCollected(string tenant, decimal amount) =>
        new(NotificationType.CashCollected, $"{PersonName.Display(tenant)}, registramos tu pago en efectivo de {Money(amount)}",
            "Gracias. Ya aparece en tu estado de cuenta.", "/portal/payments");

    // ---------- reminders ----------

    public static NotificationMessage RentDueSoon(string tenant, string charge, decimal balance, DateOnly due, int daysLeft) =>
        new(NotificationType.RentDueSoon,
            daysLeft == 0 ? $"{PersonName.Display(tenant)}, tu renta vence hoy" : $"{PersonName.Display(tenant)}, tu renta vence en {Days(daysLeft)}",
            Clip($"{charge}: {Money(balance)}, vence el {Date(due)}. Paga por transferencia o con tu código de efectivo.", 500), "/portal/pay");

    public static NotificationMessage DepositDueSoon(string tenant, string charge, decimal balance, DateOnly due, int daysLeft) =>
        new(NotificationType.RentDueSoon,
            daysLeft == 0 ? $"{PersonName.Display(tenant)}, tu abono del depósito vence hoy" : $"{PersonName.Display(tenant)}, tu abono del depósito vence en {Days(daysLeft)}",
            Clip($"{charge}: {Money(balance)}, vence el {Date(due)}. Paga por transferencia o con tu código de efectivo.", 500), "/portal/pay");

    public static NotificationMessage ChargeOverdueForTenant(string tenant, string charge, decimal balance, int daysLate) =>
        new(NotificationType.ChargeOverdue, $"{PersonName.Display(tenant)}, tienes un cargo vencido",
            Clip($"{charge}: {Money(balance)}, venció hace {Days(daysLate)}.", 500), "/portal/account");

    public static NotificationMessage ChargeOverdueForStaff(string tenant, string charge, decimal balance, int daysLate, string unitCode) =>
        new(NotificationType.ChargeOverdue, $"{PersonName.Display(tenant)} tiene un cargo vencido hace {Days(daysLate)}",
            Clip($"{charge}: {Money(balance)} · unidad {unitCode}.", 500), "/charges");

    public static NotificationMessage LeaseExpiringForTenant(string tenant, string unitCode, DateOnly end, int daysLeft) =>
        new(NotificationType.LeaseExpiring, $"{PersonName.Display(tenant)}, tu contrato termina en {Days(daysLeft)}",
            $"El contrato de la unidad {unitCode} termina el {Date(end)}. Contacta a la administración si quieres renovarlo.", "/portal");

    public static NotificationMessage LeaseExpiringForStaff(string tenant, string unitCode, DateOnly end, int daysLeft, Guid leaseId) =>
        new(NotificationType.LeaseExpiring, $"{PersonName.Display(tenant)}: su contrato termina en {Days(daysLeft)}",
            $"Unidad {unitCode} · termina el {Date(end)}.", $"/leases/{leaseId}");

    public static NotificationMessage PromiseDue(string tenant, string charge, decimal balance) =>
        new(NotificationType.PromiseDue, $"{PersonName.Display(tenant)} prometió pagar hoy",
            Clip($"{charge}: saldo {Money(balance)}. Si no paga hoy, la promesa se marcará como incumplida.", 500), "/promises");

    public static NotificationMessage PromiseBroken(string tenant, string charge, decimal balance, DateOnly promisedOn) =>
        new(NotificationType.PromiseBroken, $"{PersonName.Display(tenant)} no cumplió su promesa de pago",
            Clip($"{charge}: saldo {Money(balance)}. Prometió pagar el {Date(promisedOn)}.", 500), "/promises");
}
