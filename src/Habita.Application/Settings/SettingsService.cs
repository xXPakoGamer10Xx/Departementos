using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Domain.Settings;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Settings;

public record SettingsDto(string LandlordName, string LandlordAddress, int[] RentReminderDays, int[] ContractExpiryDays, int ReminderHour, int GraceDays, DateTimeOffset? UpdatedAt);
/// <summary>Absent field = unchanged. An empty list switches that reminder off.</summary>
public record UpdateSettingsRequest(string? LandlordName, string? LandlordAddress, int[]? RentReminderDays, int[]? ContractExpiryDays, int? ReminderHour = null, int? GraceDays = null);

public class SettingsService(IAppDbContext db, ICurrentOrganization org, IAuditWriter audit, IClock clock)
{
    public async Task<SettingsDto> GetAsync(CancellationToken ct)
    {
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return s is null ? Defaults() : ToDto(s);
    }

    public async Task<Result<SettingsDto>> UpdateAsync(UpdateSettingsRequest req, CancellationToken ct)
    {
        var name = req.LandlordName?.Trim(); var address = req.LandlordAddress?.Trim();
        if (name is { Length: > 200 }) return new(Outcome.Invalid, Error: "El nombre del arrendador no puede pasar de 200 caracteres.");
        if (address is { Length: > 500 }) return new(Outcome.Invalid, Error: "El domicilio no puede pasar de 500 caracteres.");
        int[]? reminders = null, expiry = null;
        if (req.RentReminderDays is not null && (reminders = OrganizationSettings.NormalizeDays(req.RentReminderDays, 0)) is null)
            return new(Outcome.Invalid, Error: $"Los días de recordatorio de renta van de 0 a {OrganizationSettings.MaxDays}, máximo {OrganizationSettings.MaxListLength} valores.");
        if (req.ContractExpiryDays is not null && (expiry = OrganizationSettings.NormalizeDays(req.ContractExpiryDays, 1)) is null)
            return new(Outcome.Invalid, Error: $"Los días de aviso de fin de contrato van de 1 a {OrganizationSettings.MaxDays}, máximo {OrganizationSettings.MaxListLength} valores.");

        if (req.ReminderHour is < 0 or > 23) return new(Outcome.Invalid, Error: "La hora de los recordatorios va de 0 a 23.");
        if (req.GraceDays is < 0 or > OrganizationSettings.MaxGraceDays) return new(Outcome.Invalid, Error: $"Los días de gracia van de 0 a {OrganizationSettings.MaxGraceDays}.");
        var s = await db.Settings.FirstOrDefaultAsync(ct);
        var isNew = s is null;
        s ??= new OrganizationSettings { OrganizationId = org.Id!.Value };
        var changed = new List<string>();
        if (name is not null && name != s.LandlordName) { s.LandlordName = name; changed.Add("landlordName"); }
        if (address is not null && address != s.LandlordAddress) { s.LandlordAddress = address; changed.Add("landlordAddress"); }
        if (reminders is not null && !reminders.SequenceEqual(s.RentReminderDays)) { s.RentReminderDays = reminders; changed.Add("rentReminderDays"); }
        if (expiry is not null && !expiry.SequenceEqual(s.ContractExpiryDays)) { s.ContractExpiryDays = expiry; changed.Add("contractExpiryDays"); }
        if (req.ReminderHour is { } hour && hour != s.ReminderHour) { s.ReminderHour = hour; changed.Add("reminderHour"); }
        if (req.GraceDays is { } grace && grace != s.GraceDays) { s.GraceDays = grace; changed.Add("graceDays"); }

        if (changed.Count > 0 || isNew)
        {
            s.UpdatedAt = clock.UtcNow;
            if (isNew) db.Settings.Add(s);
            audit.Write("settings.updated", nameof(OrganizationSettings), s.OrganizationId, metadata: new { fields = changed });
            await db.SaveChangesAsync(ct);
        }
        return new(Outcome.Ok, ToDto(s));
    }

    private static SettingsDto Defaults() => new("", "", OrganizationSettings.DefaultRentReminderDays, OrganizationSettings.DefaultContractExpiryDays, OrganizationSettings.DefaultReminderHour, 0, null);
    private static SettingsDto ToDto(OrganizationSettings s) => new(s.LandlordName, s.LandlordAddress, s.RentReminderDays, s.ContractExpiryDays, s.ReminderHour, s.GraceDays, s.UpdatedAt);
}
