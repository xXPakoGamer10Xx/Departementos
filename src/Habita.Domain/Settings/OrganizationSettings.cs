using Habita.Domain.Common;

namespace Habita.Domain.Settings;

/// <summary>One row per organization. Absent means "all defaults".</summary>
public class OrganizationSettings : IOrganizationOwned
{
    public const int MaxDays = 60;
    public const int MaxListLength = 5;
    public const int DefaultReminderHour = 9;
    public const int MaxGraceDays = 30;
    public static readonly int[] DefaultRentReminderDays = [3, 1, 0];
    public static readonly int[] DefaultContractExpiryDays = [30, 7, 1];

    public Guid OrganizationId { get; set; }
    /// <summary>Name of the landlord (arrendador) printed on contracts and receipts.</summary>
    public string LandlordName { get; set; } = "";
    public string LandlordAddress { get; set; } = "";
    /// <summary>Days before a charge's due date on which tenants and staff are reminded. 0 = the due day itself.</summary>
    public int[] RentReminderDays { get; set; } = DefaultRentReminderDays;
    /// <summary>Days before a lease ends on which a reminder is sent.</summary>
    public int[] ContractExpiryDays { get; set; } = DefaultContractExpiryDays;
    /// <summary>Local hour (0-23, organization time zone) from which the daily reminders are produced.</summary>
    public int ReminderHour { get; set; } = DefaultReminderHour;
    /// <summary>Days after a due date during which an unpaid charge is NOT yet overdue (0 = overdue the day after). Charges, reports, the dashboard and reminders all use it.</summary>
    public int GraceDays { get; set; }
    /// <summary>Local date of the last completed reminder run. A run is never repeated for the same date.</summary>
    public DateOnly? LastReminderRunOn { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when the local time is at or past the reminder hour and today's run has not happened yet.</summary>
    public static bool IsRunDue(int reminderHour, DateOnly? lastRunOn, DateOnly localToday, int localHour) =>
        localHour >= reminderHour && (lastRunOn is null || lastRunOn < localToday);

    /// <summary>Sorted descending, de-duplicated. Null when any value is out of range or the list is too long.</summary>
    public static int[]? NormalizeDays(IEnumerable<int>? days, int min)
    {
        if (days is null) return null;
        var list = days.Distinct().OrderByDescending(d => d).ToArray();
        return list.Length <= MaxListLength && list.All(d => d >= min && d <= MaxDays) ? list : null;
    }
}
