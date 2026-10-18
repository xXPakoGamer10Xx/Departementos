using Habita.Domain.Common;
using Habita.Domain.Properties;

namespace Habita.Domain.Deposits;

/// <summary>
/// How a lease's deposit was closed: what was held, what was kept for damages or debts (each deduction with its reason) and what went back to the tenant.
/// The money is not income while it is held, and the refund is not an expense: this record is the only place it leaves custody. It never changes after it
/// is made; a mistake is voided with a reason and made again.
/// </summary>
public class DepositSettlement : IOrganizationOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid LeaseId { get; set; }
    /// <summary>What was in custody at that moment: the deposit installments actually collected.</summary>
    public decimal HeldAmount { get; set; }
    public decimal DeductionsTotal { get; set; }
    public decimal RefundAmount { get; set; }
    public DateOnly SettledOn { get; set; }
    public string? Notes { get; set; }
    public Guid SettledByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public Guid? VoidedByUserId { get; set; }

    public bool IsActive => VoidedAt is null;

    public void Void(string reason, Guid userId, DateTimeOffset now)
    {
        if (VoidedAt is not null) throw new DomainException("deposit.already_voided", "La liquidación ya está anulada.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("deposit.reason_required", "El motivo de la anulación es obligatorio.");
        VoidedAt = now; VoidReason = reason.Trim(); VoidedByUserId = userId;
    }
}

public class DepositDeduction : IOrganizationOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid SettlementId { get; set; }
    public string Concept { get; set; } = "";
    public decimal Amount { get; set; }
    /// <summary>The order the deductions were written in, kept so they read the same way every time.</summary>
    public int SortOrder { get; set; }
}

public static class DepositRules
{
    public const int MaxInstallments = 12;
    public const int MaxDeductions = 20;

    public static string? ValidateInstallment(decimal amount, DateOnly dueDate)
    {
        if (amount <= 0 || amount > Unit.MaxRent) return "Cada abono debe ser mayor que cero y no superar 99,999,999.99.";
        if (decimal.Round(amount, 2) != amount) return "Cada abono admite máximo 2 decimales.";
        if (dueDate.Year < 2000 || dueDate.Year > 2100) return "Las fechas de los abonos deben estar entre 2000 y 2100.";
        return null;
    }

    /// <summary>The refund is what is left of the held deposit after the deductions. Null (and the problem) when the deductions are not acceptable.</summary>
    public static string? ValidateSettlement(decimal held, IReadOnlyList<(string? Concept, decimal Amount)> deductions, DateOnly settledOn, DateOnly today, string? notes)
    {
        if (held <= 0) return "No hay depósito cobrado que liquidar.";
        if (deductions.Count > MaxDeductions) return $"Se admiten hasta {MaxDeductions} deducciones.";
        foreach (var (concept, amount) in deductions)
        {
            if (string.IsNullOrWhiteSpace(concept) || concept.Trim().Length > 200) return "Cada deducción necesita su motivo (hasta 200 caracteres).";
            if (amount <= 0 || decimal.Round(amount, 2) != amount) return "Cada deducción debe ser mayor que cero y tener máximo 2 decimales.";
        }
        var total = deductions.Sum(d => d.Amount);
        if (total > held) return $"Las deducciones ({total:N2}) no pueden superar el depósito cobrado ({held:N2}).";
        if (settledOn > today) return "La fecha de la liquidación no puede ser futura.";
        if (settledOn.Year < 2000) return "La fecha de la liquidación no es válida.";
        if (notes is { Length: > 1000 }) return "Las notas no pueden superar 1000 caracteres.";
        return null;
    }
}
