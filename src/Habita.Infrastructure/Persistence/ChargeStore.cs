using Habita.Application.Abstractions;
using Habita.Domain.Charges;
using Microsoft.EntityFrameworkCore;

namespace Habita.Infrastructure.Persistence;

public class ChargeStore(HabitaDbContext db, ICurrentOrganization org, IClock clock) : IChargeStore
{
    public async Task<IReadOnlyList<Guid>> InsertRentChargesIgnoringDuplicatesAsync(IReadOnlyList<RentCharge> charges, CancellationToken ct)
    {
        // Raw SQL bypasses the EF tenant interceptor, so the organization is stamped here from the authenticated context;
        // the composite foreign key to Leases still rejects any lease that is not this organization's.
        var orgId = org.Id ?? throw new InvalidOperationException("No hay organización activa.");
        var inserted = new List<Guid>();
        foreach (var c in charges)
        {
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "RentCharges" ("Id","OrganizationId","LeaseId","Type","PeriodYear","PeriodMonth","Description","Amount","DueDate","Status","CreatedAt","UpdatedAt")
                VALUES ({c.Id}, {orgId}, {c.LeaseId}, 'Rent', {c.PeriodYear}, {c.PeriodMonth}, {c.Description}, {c.Amount}, {c.DueDate}, 'Open', {clock.UtcNow}, {clock.UtcNow})
                ON CONFLICT ("LeaseId","PeriodYear","PeriodMonth") WHERE "Type" = 'Rent' AND "Status" <> 'Voided' DO NOTHING
                """, ct);
            if (affected == 1) inserted.Add(c.Id);
        }
        return inserted;
    }
}

public class DbTransactions(HabitaDbContext db) : IDbTransactions
{
    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return await work(); // join the outer transaction
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await work();
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
