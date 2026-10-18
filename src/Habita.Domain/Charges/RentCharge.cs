using Habita.Domain.Common;
using Habita.Domain.Properties;

namespace Habita.Domain.Charges;

public enum ChargeType { Rent, Other, Deposit }

/// <summary>Stored: Open, PartiallyPaid, Paid, Voided. Overdue is never stored: it is derived from the due date and the organization's local date.</summary>
public enum ChargeStatus { Open, PartiallyPaid, Paid, Overdue, Voided }

public class RentCharge : IOrganizationOwned
{
    private static readonly string[] MonthNames =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid LeaseId { get; set; }
    public ChargeType Type { get; set; }
    public int? PeriodYear { get; set; }
    public int? PeriodMonth { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public DateOnly DueDate { get; set; }
    public ChargeStatus Status { get; set; } = ChargeStatus.Open;
    public string? VoidReason { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static (DateOnly First, DateOnly Last) PeriodRange(int year, int month) =>
        (new DateOnly(year, month, 1), new DateOnly(year, month, DateTime.DaysInMonth(year, month)));

    /// <summary>DueDay is limited to 1..28 on leases, so the date always exists.</summary>
    public static DateOnly DueDateFor(int year, int month, int dueDay) => new(year, month, dueDay);

    public static string RentDescription(int year, int month) => $"Renta {MonthNames[month - 1]} {year}";

    public static string? ValidatePeriod(int year, int month) =>
        year is < 2000 or > 2100 ? "El año debe estar entre 2000 y 2100." : month is < 1 or > 12 ? "El mes debe estar entre 1 y 12." : null;

    public static string? ValidateManual(string description, decimal amount, int? periodYear, int? periodMonth)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > 200) return "La descripción es obligatoria y no puede superar 200 caracteres.";
        if (amount <= 0 || amount > Unit.MaxRent) return "El importe debe ser mayor que cero y no superar 99,999,999.99.";
        if (decimal.Round(amount, 2) != amount) return "El importe admite máximo 2 decimales.";
        if ((periodYear is null) != (periodMonth is null)) return "Indica el año y el mes del periodo, o ninguno.";
        if (periodYear is { } y && periodMonth is { } m && ValidatePeriod(y, m) is { } e) return e;
        return null;
    }

    /// <summary>Overdue = positive balance and the organization's local today is after the due date.</summary>
    public static ChargeStatus Effective(ChargeStatus stored, DateOnly dueDate, DateOnly today, int graceDays = 0) =>
        stored is ChargeStatus.Open or ChargeStatus.PartiallyPaid && today > dueDate.AddDays(graceDays) ? ChargeStatus.Overdue : stored;

    /// <summary>A charge due BEFORE this date is overdue: the local today minus the grace days. The same rule as <see cref="Effective"/>, written for queries.</summary>
    public static DateOnly OverdueBefore(DateOnly today, int graceDays) => today.AddDays(-graceDays);

    /// <summary>Balance never goes negative; a voided charge has no balance.</summary>
    public static decimal Balance(decimal amount, decimal allocated, bool voided) => voided ? 0m : Math.Max(0m, amount - allocated);

    /// <summary>Base status from activity: zero balance is Paid, any allocation is PartiallyPaid, none is Open.</summary>
    public static ChargeStatus StatusFor(decimal amount, decimal allocated) =>
        allocated >= amount ? ChargeStatus.Paid : allocated > 0 ? ChargeStatus.PartiallyPaid : ChargeStatus.Open;

    public void Void(string reason, DateTimeOffset now)
    {
        if (Status == ChargeStatus.Voided) throw new DomainException("charge.already_voided", "El cargo ya está anulado.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("charge.reason_required", "El motivo de la anulación es obligatorio.");
        Status = ChargeStatus.Voided;
        VoidReason = reason.Trim();
        VoidedAt = now;
    }
}
