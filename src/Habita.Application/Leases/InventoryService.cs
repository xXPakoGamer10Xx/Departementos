using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Domain.Leases;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Leases;

public record InventoryItemDto(Guid Id, string Name, int Quantity, ItemCondition Condition, string? Notes, ItemCondition? ReturnCondition, string? ReturnNotes);
/// <summary>Id present = an item that already exists (kept, so a later return condition stays attached); absent = a new one.</summary>
public record InventoryItemInput(Guid? Id, string? Name, int Quantity, ItemCondition Condition, string? Notes);
public record ReplaceInventoryRequest(List<InventoryItemInput>? Items);
public record ReturnInput(Guid ItemId, ItemCondition Condition, string? Notes);
public record RecordReturnRequest(List<ReturnInput>? Items);

/// <summary>
/// What was handed over with the unit and in what condition (the delivery record), and later the condition when it came back. The list is replaced as a whole
/// while the lease is not over; the return is written once the lease has ended.
/// </summary>
public class InventoryService(IAppDbContext db, IDbTransactions tx, IFinanceLocks locks, IAuditWriter audit)
{
    public async Task<Result<List<InventoryItemDto>>> ListAsync(Guid leaseId, CancellationToken ct)
    {
        if (!await db.Leases.AnyAsync(l => l.Id == leaseId, ct)) return new(Outcome.NotFound);
        return new(Outcome.Ok, await LoadAsync(leaseId, ct));
    }

    public async Task<Result<List<InventoryItemDto>>> ReplaceAsync(Guid leaseId, ReplaceInventoryRequest req, CancellationToken ct)
    {
        var inputs = req.Items ?? [];
        if (inputs.Count > InventoryItem.MaxItems) return new(Outcome.Invalid, Error: $"El inventario admite hasta {InventoryItem.MaxItems} artículos.");
        foreach (var i in inputs)
            if (InventoryItem.Validate(i.Name, i.Quantity, i.Condition, i.Notes?.Trim()) is { } error) return new(Outcome.Invalid, Error: error);
        if (inputs.Where(i => i.Id is not null).Select(i => i.Id).Distinct().Count() != inputs.Count(i => i.Id is not null)) return new(Outcome.Invalid, Error: "Un artículo aparece dos veces en la lista.");

        return await tx.RunAsync<Result<List<InventoryItemDto>>>(async () =>
        {
            await locks.LockScopeAsync($"inventory:{leaseId}", ct);
            var lease = await db.Leases.AsNoTracking().Where(l => l.Id == leaseId).Select(l => new { l.Status }).FirstOrDefaultAsync(ct);
            if (lease is null) return new(Outcome.NotFound);
            if (lease.Status is LeaseStatus.Ended or LeaseStatus.Cancelled) return new(Outcome.Conflict, Error: "Un contrato terminado o cancelado ya no cambia su inventario de entrega.");

            var existing = await db.InventoryItems.Where(i => i.LeaseId == leaseId).ToListAsync(ct);
            var keep = inputs.Where(i => i.Id is not null).Select(i => i.Id!.Value).ToHashSet();
            if (keep.Except(existing.Select(e => e.Id)).Any()) return new(Outcome.Invalid, Error: "Algún artículo no pertenece a este contrato.");
            db.InventoryItems.RemoveRange(existing.Where(e => !keep.Contains(e.Id)));

            for (var order = 0; order < inputs.Count; order++)
            {
                var i = inputs[order];
                var item = i.Id is { } id ? existing.First(e => e.Id == id) : new InventoryItem { LeaseId = leaseId };
                (item.Name, item.Quantity, item.Condition, item.Notes, item.SortOrder) = (i.Name!.Trim(), i.Quantity, i.Condition, string.IsNullOrWhiteSpace(i.Notes) ? null : i.Notes.Trim(), order);
                if (i.Id is null) db.InventoryItems.Add(item);
            }
            audit.Write("inventory.updated", nameof(InventoryItem), leaseId, metadata: new { items = inputs.Count });
            await db.SaveChangesAsync(ct);
            return new(Outcome.Ok, await LoadAsync(leaseId, ct));
        }, ct);
    }

    public async Task<Result<List<InventoryItemDto>>> RecordReturnAsync(Guid leaseId, RecordReturnRequest req, CancellationToken ct)
    {
        var inputs = req.Items ?? [];
        if (inputs.Count == 0) return new(Outcome.Invalid, Error: "Indica el estado de al menos un artículo.");
        foreach (var i in inputs)
        {
            if (!Enum.IsDefined(i.Condition)) return new(Outcome.Invalid, Error: "El estado del artículo no es válido (bueno, regular o malo).");
            if (i.Notes is { Length: > 300 }) return new(Outcome.Invalid, Error: "La nota de un artículo no puede superar 300 caracteres.");
        }
        var lease = await db.Leases.AsNoTracking().Where(l => l.Id == leaseId).Select(l => new { l.Status }).FirstOrDefaultAsync(ct);
        if (lease is null) return new(Outcome.NotFound);
        if (lease.Status != LeaseStatus.Ended) return new(Outcome.Conflict, Error: "El estado de devolución se registra cuando el contrato ya terminó.");

        var items = await db.InventoryItems.Where(i => i.LeaseId == leaseId).ToListAsync(ct);
        foreach (var i in inputs)
        {
            var item = items.FirstOrDefault(x => x.Id == i.ItemId);
            if (item is null) return new(Outcome.Invalid, Error: "Algún artículo no pertenece a este contrato.");
            item.ReturnCondition = i.Condition; item.ReturnNotes = string.IsNullOrWhiteSpace(i.Notes) ? null : i.Notes.Trim();
        }
        audit.Write("inventory.returned", nameof(InventoryItem), leaseId, metadata: new { items = inputs.Count });
        await db.SaveChangesAsync(ct);
        return new(Outcome.Ok, await LoadAsync(leaseId, ct));
    }

    private async Task<List<InventoryItemDto>> LoadAsync(Guid leaseId, CancellationToken ct) =>
        (await db.InventoryItems.AsNoTracking().Where(i => i.LeaseId == leaseId).OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToListAsync(ct))
            .Select(i => new InventoryItemDto(i.Id, i.Name, i.Quantity, i.Condition, i.Notes, i.ReturnCondition, i.ReturnNotes)).ToList();
}
