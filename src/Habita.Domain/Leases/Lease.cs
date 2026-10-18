using Habita.Domain.Common;
using Habita.Domain.Properties;

namespace Habita.Domain.Leases;

public enum LeaseStatus { Draft, Active, Ended, Cancelled }

public class Lease : IOrganizationOwned
{
    public const int MinDueDay = 1;
    public const int MaxDueDay = 28; // avoids ambiguity in short months

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UnitId { get; set; }
    public Guid TenantId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public decimal MonthlyRent { get; set; }
    public int DueDay { get; set; }
    public decimal? DepositAmount { get; set; }
    public LeaseStatus Status { get; set; } = LeaseStatus.Draft;
    public string? TermsNotes { get; set; }
    /// <summary>Optional guarantor (fiador). Only a name and a phone: the minimum a contract needs.</summary>
    public string? GuarantorName { get; set; }
    public string? GuarantorPhone { get; set; }
    public string? ClosureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>A lease reserves its unit while Active or Ended: an ended lease keeps its range up to the (possibly future) end date.</summary>
    public bool InForce => Status is LeaseStatus.Active or LeaseStatus.Ended;

    public bool IsCurrent(DateOnly today) => InForce && StartDate <= today && (EndDate is null || EndDate >= today);

    public bool Overlaps(DateOnly start, DateOnly? end) =>
        StartDate <= (end ?? DateOnly.MaxValue) && (EndDate ?? DateOnly.MaxValue) >= start;

    public static string? ValidateTerms(DateOnly start, DateOnly? end, decimal monthlyRent, int dueDay, decimal? deposit, string? termsNotes)
    {
        if (end is { } e && e < start) return "La fecha final no puede ser anterior a la fecha de inicio.";
        if (monthlyRent < 0 || monthlyRent > Unit.MaxRent) return "La renta mensual debe ser un importe entre 0 y 99,999,999.99.";
        if (decimal.Round(monthlyRent, 2) != monthlyRent) return "La renta mensual admite máximo 2 decimales.";
        if (dueDay is < MinDueDay or > MaxDueDay) return $"El día de vencimiento debe estar entre {MinDueDay} y {MaxDueDay}.";
        if (deposit is { } d && (d < 0 || d > Unit.MaxRent || decimal.Round(d, 2) != d)) return "El depósito debe ser un importe válido, de 0 a 99,999,999.99 con máximo 2 decimales.";
        if (termsNotes is { Length: > 4000 }) return "Las condiciones no pueden superar 4000 caracteres.";
        return null;
    }

    public static string? ValidateGuarantor(string? name, string? phone)
    {
        if (name is { Length: > 200 }) return "El nombre del fiador no puede superar 200 caracteres.";
        if (phone is { Length: > 30 }) return "El teléfono del fiador no puede superar 30 caracteres.";
        if (!string.IsNullOrWhiteSpace(phone) && !Tenants.Tenant.IsValidPhone(phone.Trim())) return "El teléfono del fiador no es válido.";
        if (!string.IsNullOrWhiteSpace(phone) && string.IsNullOrWhiteSpace(name)) return "Indica el nombre del fiador junto con su teléfono.";
        return null;
    }

    public void Activate()
    {
        if (Status != LeaseStatus.Draft) throw new DomainException("lease.invalid_transition", "Solo un contrato en borrador puede activarse.");
        Status = LeaseStatus.Active;
    }

    public void Terminate(DateOnly endDate, string reason)
    {
        if (Status != LeaseStatus.Active) throw new DomainException("lease.invalid_transition", "Solo un contrato vigente puede terminarse.");
        if (endDate < StartDate) throw new DomainException("lease.invalid_date", "La fecha de terminación no puede ser anterior al inicio del contrato.");
        if (EndDate is { } current && endDate > current) throw new DomainException("lease.invalid_date", "La terminación no puede ser posterior a la fecha final pactada.");
        EndDate = endDate;
        ClosureReason = reason;
        Status = LeaseStatus.Ended;
    }

    public void Cancel(string reason)
    {
        if (Status is not (LeaseStatus.Draft or LeaseStatus.Active)) throw new DomainException("lease.invalid_transition", "Solo un contrato en borrador o vigente puede cancelarse.");
        ClosureReason = reason;
        Status = LeaseStatus.Cancelled;
    }
}
