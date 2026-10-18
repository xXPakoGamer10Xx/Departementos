using Habita.Application.Abstractions;
using Habita.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Habita.Infrastructure.Services;

public class UserDirectory(HabitaDbContext db) : IUserDirectory
{
    public async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        // The Membership query filter limits this to members of the current organization (deactivated ones included, for history).
        return await (from m in db.Memberships join u in db.Users on m.UserId equals u.Id where ids.Contains(u.Id) select new { u.Id, u.FullName })
            .ToDictionaryAsync(x => x.Id, x => x.FullName, ct);
    }

    public async Task<Dictionary<Guid, string>> EmailsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        return await (from m in db.Memberships join u in db.Users on m.UserId equals u.Id where ids.Contains(u.Id) && u.Email != null select new { u.Id, Email = u.Email! })
            .ToDictionaryAsync(x => x.Id, x => x.Email, ct);
    }
}
