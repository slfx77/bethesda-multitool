namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Inventory item information from CNTO subrecord, optionally extended with COED
///     ownership data (Owner FormID + Global-or-Rank + ItemCondition).
/// </summary>
public record InventoryItem(uint ItemFormId, int Count)
{
    /// <summary>Owner FormID from COED subrecord (NPC, faction, or 0 if unowned).</summary>
    public uint? OwnerFormId { get; init; }

    /// <summary>Required global variable FormID or NPC rank (semantics depend on owner type).</summary>
    public uint? GlobalOrRank { get; init; }

    /// <summary>Item condition (float 0.0–1.0) from COED subrecord. Negative when unset.</summary>
    public float? ItemCondition { get; init; }
}
