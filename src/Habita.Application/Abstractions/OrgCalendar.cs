using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Abstractions;

/// <summary>"Today" as the organization's local date (contract dates and due dates are local dates without time).</summary>
public class OrgCalendar(IAppDbContext db, ICurrentOrganization org, IClock clock)
{
    private DateOnly? _today;
    private int? _grace;

    private TimeZoneInfo? _tz;

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct)
    {
        if (_tz is not null) return _tz;
        var tzId = await db.Organizations.Where(o => o.Id == org.Id).Select(o => o.TimeZone).FirstOrDefaultAsync(ct);
        try { return _tz = TimeZoneInfo.FindSystemTimeZoneById(tzId ?? "UTC"); }
        catch (TimeZoneNotFoundException) { return _tz = TimeZoneInfo.Utc; }
    }

    /// <summary>The organization's grace days: how long after a due date a charge is not yet overdue.</summary>
    public async Task<int> GraceDaysAsync(CancellationToken ct) =>
        _grace ??= await db.Settings.AsNoTracking().Select(s => (int?)s.GraceDays).FirstOrDefaultAsync(ct) ?? 0;

    /// <summary>Charges due before this date are overdue (today minus the grace days).</summary>
    public async Task<DateOnly> OverdueBeforeAsync(CancellationToken ct) => (await TodayAsync(ct)).AddDays(-await GraceDaysAsync(ct));

    /// <summary>The organization's wall-clock time right now (used to decide whether the daily reminder hour has arrived).</summary>
    public async Task<DateTime> LocalNowAsync(CancellationToken ct) => TimeZoneInfo.ConvertTime(clock.UtcNow, await ZoneAsync(ct)).DateTime;

    public async Task<DateOnly> TodayAsync(CancellationToken ct)
    {
        if (_today is { } cached) return cached;
        return (_today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, await ZoneAsync(ct)).Date)).Value;
    }

    /// <summary>The organization's local date of an instant (a receipt issued at 11 p.m. local time belongs to that day, not to the next one in UTC).</summary>
    public async Task<DateOnly> LocalDateOfAsync(DateTimeOffset instant, CancellationToken ct) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, await ZoneAsync(ct)).Date);

    /// <summary>The UTC instant at which the given local day begins in the organization's time zone.</summary>
    public async Task<DateTimeOffset> StartOfDayUtcAsync(DateOnly day, CancellationToken ct)
    {
        var tz = await ZoneAsync(ct);
        var local = day.ToDateTime(TimeOnly.MinValue);
        if (tz.IsInvalidTime(local)) local = local.AddHours(1); // a day can start inside a DST gap
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, tz), TimeSpan.Zero);
    }
}
