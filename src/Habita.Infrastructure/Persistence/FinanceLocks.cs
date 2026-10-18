using Habita.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Habita.Infrastructure.Persistence;

/// <summary>
/// Pessimistic locks for money movements. Raw SQL bypasses the EF tenant filter, so every statement also
/// restricts by the authenticated organization. Callers must hold an open transaction (see IDbTransactions).
/// </summary>
public class FinanceLocks(HabitaDbContext db, ICurrentOrganization org) : IFinanceLocks
{
    private Guid OrgId => org.Id ?? throw new InvalidOperationException("No hay organización activa.");

    public async Task LockScopeAsync(string scope, CancellationToken ct)
    {
        var scoped = $"{OrgId}:scope:{scope}"; // a different namespace from idempotency keys, so a client-chosen key can never collide with a scope
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({scoped}, 0))", ct);
    }

    public async Task<bool> TryLockScopeAsync(string scope, CancellationToken ct)
    {
        var scoped = $"{OrgId}:scope:{scope}";
        return await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({scoped}, 0)) AS \"Value\"").SingleAsync(ct);
    }

    public async Task LockClaimAsync(Guid claimId, CancellationToken ct)
    {
        var orgId = OrgId;
        await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "PaymentClaims" WHERE "Id" = {claimId} AND "OrganizationId" = {orgId} FOR UPDATE""", ct);
    }

    public async Task LockCashTokenAsync(Guid tokenId, CancellationToken ct)
    {
        var orgId = OrgId;
        await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "CashTokens" WHERE "Id" = {tokenId} AND "OrganizationId" = {orgId} FOR UPDATE""", ct);
    }

    public async Task LockIdempotencyKeyAsync(string key, CancellationToken ct)
    {
        var scoped = $"{OrgId}:{key}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({scoped}, 0))", ct);
    }

    public async Task LockPaymentAsync(Guid paymentId, CancellationToken ct)
    {
        var orgId = OrgId;
        await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Payments" WHERE "Id" = {paymentId} AND "OrganizationId" = {orgId} FOR UPDATE""", ct);
    }

    public async Task LockChargesAsync(IReadOnlyCollection<Guid> chargeIds, CancellationToken ct)
    {
        if (chargeIds.Count == 0) return;
        var orgId = OrgId;
        var ids = chargeIds.Distinct().OrderBy(id => id).ToArray();
        // ORDER BY + FOR UPDATE: rows are locked in id order, so concurrent requests cannot deadlock each other.
        await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT "Id" FROM "RentCharges" WHERE "Id" = ANY({ids}) AND "OrganizationId" = {orgId} ORDER BY "Id" FOR UPDATE""", ct);
    }
}
