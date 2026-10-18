using Habita.Domain.Common;
using Habita.Domain.Properties;

namespace Habita.Domain.Payments;

public enum PaymentMethod { Cash, BankTransfer, Card, Other }
public enum PaymentStatus { Confirmed, Reversed }

public class Payment : IOrganizationOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>Local date the money was received (organization time zone). Income reports are by this date.</summary>
    public DateOnly ReceivedOn { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Confirmed;
    public Guid CreatedByUserId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? RequestHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReversedAt { get; set; }
    public string? ReversalReason { get; set; }
    public Guid? ReversedByUserId { get; set; }

    public static string? ValidateNew(decimal amount, DateOnly receivedOn, DateOnly today, string? reference, string? notes)
    {
        if (amount <= 0 || amount > Unit.MaxRent) return "El importe debe ser mayor que cero y no superar 99,999,999.99.";
        if (decimal.Round(amount, 2) != amount) return "El importe admite máximo 2 decimales.";
        if (receivedOn > today) return "La fecha de recepción no puede ser futura.";
        if (receivedOn.Year < 2000) return "La fecha de recepción no es válida.";
        if (reference is { Length: > 100 }) return "La referencia no puede superar 100 caracteres.";
        if (notes is { Length: > 2000 }) return "Las notas no pueden superar 2000 caracteres.";
        return null;
    }

    /// <summary>Unassigned balance (saldo a favor). Never negative.</summary>
    public static decimal Unassigned(decimal amount, decimal allocated) => Math.Max(0m, amount - allocated);

    public void Reverse(string reason, Guid userId, DateTimeOffset now)
    {
        if (Status == PaymentStatus.Reversed) throw new DomainException("payment.already_reversed", "El pago ya está revertido.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("payment.reason_required", "El motivo del reverso es obligatorio.");
        Status = PaymentStatus.Reversed;
        ReversalReason = reason.Trim();
        ReversedAt = now;
        ReversedByUserId = userId;
    }
}

public class PaymentAllocation : IOrganizationOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid PaymentId { get; set; }
    public Guid RentChargeId { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReversedAt { get; set; }
    public string? ReversalReason { get; set; }

    public bool IsActive => ReversedAt is null;

    public static string? ValidateAmount(decimal amount) =>
        amount <= 0 ? "El importe aplicado debe ser mayor que cero." : decimal.Round(amount, 2) != amount ? "El importe aplicado admite máximo 2 decimales." : null;

    public void Reverse(string reason, DateTimeOffset now)
    {
        if (ReversedAt is not null) throw new DomainException("allocation.already_reversed", "La aplicación ya está revertida.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("allocation.reason_required", "El motivo del reverso es obligatorio.");
        ReversedAt = now;
        ReversalReason = reason.Trim();
    }
}
