using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils;

/// <summary>
///     Visual category for placed objects in the World tab, used for color-coding.
/// </summary>
internal enum PlacedObjectCategory
{
    Static,
    Plant,
    Door,
    Activator,
    Light,
    Furniture,
    Npc,
    Creature,
    Container,
    Item,
    MapMarker,
    Unknown
}

/// <summary>
///     Builds a FormID to PlacedObjectCategory lookup from all record types.
/// </summary>
internal static class PlacedObjectCategoryIndex
{
    public static Dictionary<uint, PlacedObjectCategory> Build(RecordCollection records)
    {
        var index = new Dictionary<uint, PlacedObjectCategory>();

        foreach (var r in records.Statics) { index.TryAdd(r.FormId, PlacedObjectCategory.Static); }
        foreach (var r in records.Doors) { index.TryAdd(r.FormId, PlacedObjectCategory.Door); }
        foreach (var r in records.Activators) { index.TryAdd(r.FormId, PlacedObjectCategory.Activator); }
        foreach (var r in records.Lights) { index.TryAdd(r.FormId, PlacedObjectCategory.Light); }
        foreach (var r in records.Furniture) { index.TryAdd(r.FormId, PlacedObjectCategory.Furniture); }
        foreach (var r in records.Npcs) { index.TryAdd(r.FormId, PlacedObjectCategory.Npc); }
        foreach (var r in records.Creatures) { index.TryAdd(r.FormId, PlacedObjectCategory.Creature); }
        foreach (var r in records.Containers) { index.TryAdd(r.FormId, PlacedObjectCategory.Container); }
        foreach (var r in records.Weapons) { index.TryAdd(r.FormId, PlacedObjectCategory.Item); }
        foreach (var r in records.Armor) { index.TryAdd(r.FormId, PlacedObjectCategory.Item); }
        foreach (var r in records.Ammo) { index.TryAdd(r.FormId, PlacedObjectCategory.Item); }
        foreach (var r in records.Consumables) { index.TryAdd(r.FormId, PlacedObjectCategory.Item); }
        foreach (var r in records.MiscItems) { index.TryAdd(r.FormId, PlacedObjectCategory.Item); }
        foreach (var r in records.Books) { index.TryAdd(r.FormId, PlacedObjectCategory.Item); }

        return index;
    }
}
