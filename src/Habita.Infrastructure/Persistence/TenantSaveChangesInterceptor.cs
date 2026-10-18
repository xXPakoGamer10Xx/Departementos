using Habita.Application.Abstractions;
using Habita.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Habita.Infrastructure.Persistence;

/// <summary>Stamps OrganizationId on inserts and rejects any cross-tenant write or OrganizationId change.</summary>
public class TenantSaveChangesInterceptor(ICurrentOrganization org) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Apply(eventData.Context!);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context!);
        return base.SavingChanges(eventData, result);
    }

    private void Apply(DbContext ctx)
    {
        foreach (var entry in ctx.ChangeTracker.Entries<IOrganizationOwned>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.OrganizationId == Guid.Empty && org.Id is { } id) entry.Entity.OrganizationId = id;
                    if (entry.Entity.OrganizationId == Guid.Empty)
                        throw new InvalidOperationException("No hay organización activa para guardar este registro.");
                    if (org.Id is { } current && entry.Entity.OrganizationId != current)
                        throw new InvalidOperationException("Escritura entre organizaciones no permitida.");
                    break;
                case EntityState.Modified or EntityState.Deleted:
                    if (entry.Property(nameof(IOrganizationOwned.OrganizationId)).IsModified)
                        throw new InvalidOperationException("OrganizationId no puede modificarse.");
                    if (org.Id is { } cur && entry.Entity.OrganizationId != cur)
                        throw new InvalidOperationException("Escritura entre organizaciones no permitida.");
                    break;
            }
        }
    }
}
