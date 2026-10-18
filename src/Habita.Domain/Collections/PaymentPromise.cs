using Habita.Domain.Common;

namespace Habita.Domain.Collections;

public enum PromiseStatus { Pending, Kept, Broken, Cancelled }

/// <summary>"I will pay this charge by this date." The status is derived from the money actually received, never stored.</summary>
public class PaymentPromise : IOrganizationOwned
{
    public const int MaxDaysAhead = 90;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ChargeId { get; set; }
    public DateOnly PromisedOn { get; set; }
    public string? Note { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    public static string? Validate(DateOnly promisedOn, DateOnly today, string? note)
    {
        if (promisedOn < today) return "La fecha prometida no puede ser pasada.";
        if (promisedOn > today.AddDays(MaxDaysAhead)) return $"La fecha prometida no puede pasar de {MaxDaysAhead} días.";
        if (note is { Length: > 500 }) return "La nota no puede superar 500 caracteres.";
        return null;
    }

    /// <summary>
    /// Kept: the charge was covered by payments received on or before the promised day (even early).
    /// Broken: the day has passed and it was not. Pending otherwise. A cancelled promise, or one on a voided charge, is Cancelled.
    /// </summary>
    public static PromiseStatus StatusFor(DateOnly promisedOn, DateOnly today, decimal chargeAmount, decimal paidOnOrBeforePromisedDay, bool chargeVoided, bool cancelled)
    {
        if (cancelled || chargeVoided) return PromiseStatus.Cancelled;
        if (paidOnOrBeforePromisedDay >= chargeAmount) return PromiseStatus.Kept;
        return today > promisedOn ? PromiseStatus.Broken : PromiseStatus.Pending;
    }

    public void Cancel(string reason, DateTimeOffset now)
    {
        if (CancelledAt is not null) throw new DomainException("promise.already_cancelled", "La promesa ya está cancelada.");
        CancelledAt = now; CancelReason = reason.Trim();
    }
}
