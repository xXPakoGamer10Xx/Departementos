using Habita.Domain.Common;

namespace Habita.Domain.Leases;

public enum ItemCondition { Good, Fair, Poor }

/// <summary>
/// One thing handed over with the unit, with its condition at delivery (the delivery record). When the lease ends the condition on return can be
/// written next to it, so the two are compared on the same line.
/// </summary>
public class InventoryItem : IOrganizationOwned
{
    public const int MaxItems = 100;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid LeaseId { get; set; }
    public string Name { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public ItemCondition Condition { get; set; } = ItemCondition.Good;
    public string? Notes { get; set; }
    public ItemCondition? ReturnCondition { get; set; }
    public string? ReturnNotes { get; set; }
    public int SortOrder { get; set; }

    public static string? Validate(string? name, int quantity, ItemCondition condition, string? notes)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) return "Cada artículo necesita un nombre de hasta 100 caracteres.";
        if (quantity is < 1 or > 999) return "La cantidad de cada artículo va de 1 a 999.";
        if (!Enum.IsDefined(condition)) return "El estado del artículo no es válido (bueno, regular o malo).";
        if (notes is { Length: > 300 }) return "La nota de un artículo no puede superar 300 caracteres.";
        return null;
    }

    public static string Label(ItemCondition c) => c switch { ItemCondition.Good => "Bueno", ItemCondition.Fair => "Regular", _ => "Malo" };
}
