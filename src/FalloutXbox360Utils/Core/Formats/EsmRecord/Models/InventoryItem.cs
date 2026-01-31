namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Inventory item information from CNTO subrecord.
/// </summary>
public record InventoryItem(uint ItemFormId, int Count);
