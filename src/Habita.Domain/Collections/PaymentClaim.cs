using Habita.Domain.Common;
using Habita.Domain.Payments;
using Habita.Domain.Properties;

namespace Habita.Domain.Collections;

public enum ClaimStatus { Pending, Confirmed, Rejected, Cancelled }

/// <summary>
/// A tenant says "I paid this" and attaches the proof. Nothing is recorded as income until staff confirm it, which creates a real Payment.
/// Once it leaves Pending it never changes again (a database trigger enforces it).
/// </summary>
public class PaymentClaim : IOrganizationOwned
{
    public const int MaxPendingPerTenant = 5;
    public static readonly PaymentMethod[] ReportableMethods = [PaymentMethod.BankTransfer, PaymentMethod.Cash, PaymentMethod.Other];

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Local date the tenant says they paid.</summary>
    public DateOnly PaidOn { get; set; }
    public PaymentMethod Method { get; set; }
    public string? Reference { get; set; }
    public string? TenantNote { get; set; }
    public Guid FileId { get; set; }
    public ClaimStatus Status { get; set; } = ClaimStatus.Pending;
    public Guid SubmittedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    /// <summary>Why it was rejected. The tenant reads it.</summary>
    public string? ReviewComment { get; set; }
    public Guid? PaymentId { get; set; }

    public static string? Validate(decimal amount, DateOnly paidOn, DateOnly today, PaymentMethod method, string? reference, string? note)
    {
        if (!ReportableMethods.Contains(method)) return "Elige transferencia, efectivo u otro como método.";
        if (paidOn < today.AddDays(-365)) return "La fecha de pago no puede ser de hace más de un año.";
        if (note is { Length: > 500 }) return "La nota no puede superar 500 caracteres.";
        return Payment.ValidateNew(amount, paidOn, today, reference, note);
    }

    public static string? ValidateRejection(string? comment) =>
        string.IsNullOrWhiteSpace(comment) ? "Explica al inquilino por qué se rechaza." : comment.Trim().Length > 500 ? "El comentario no puede superar 500 caracteres." : null;

    public void Confirm(Guid paymentId, Guid userId, DateTimeOffset now)
    {
        RequirePending();
        Status = ClaimStatus.Confirmed; PaymentId = paymentId; ReviewedByUserId = userId; ReviewedAt = now;
    }

    public void Reject(string comment, Guid userId, DateTimeOffset now)
    {
        RequirePending();
        if (ValidateRejection(comment) is { } error) throw new DomainException("claim.comment_required", error);
        Status = ClaimStatus.Rejected; ReviewComment = comment.Trim(); ReviewedByUserId = userId; ReviewedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        RequirePending();
        Status = ClaimStatus.Cancelled; ReviewedAt = now;
    }

    private void RequirePending()
    {
        if (Status != ClaimStatus.Pending) throw new DomainException("claim.not_pending", "Este reporte ya fue atendido.");
    }
}
