using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds a FormID to ObjectBounds lookup from all record types that have OBND.
///     Used by the World tab to draw bounding rectangles for placed objects.
/// </summary>
internal static class ObjectBoundsIndex
{
    public static Dictionary<uint, ObjectBounds> Build(RecordCollection records)
    {
        var (bounds, _) = BuildCombined(records);
        return bounds;
    }

    /// <summary>
    ///     Builds both ObjectBounds and PlacedObjectCategory indexes in a single pass
    ///     over the record collections, avoiding redundant iteration.
    /// </summary>
    public static (Dictionary<uint, ObjectBounds> Bounds, Dictionary<uint, PlacedObjectCategory> Categories)
        BuildCombined(RecordCollection records)
    {
        var bounds = new Dictionary<uint, ObjectBounds>();
        var categories = new Dictionary<uint, PlacedObjectCategory>();

        // World objects (have bounds + category)
        Process(records.Statics, s => (s.FormId, s.Bounds), PlacedObjectCategory.Static, bounds, categories);
        Process(records.Activators, a => (a.FormId, a.Bounds), PlacedObjectCategory.Activator, bounds, categories);
        Process(records.Doors, d => (d.FormId, d.Bounds), PlacedObjectCategory.Door, bounds, categories);
        Process(records.Lights, l => (l.FormId, l.Bounds), PlacedObjectCategory.Light, bounds, categories);
        Process(records.Furniture, f => (f.FormId, f.Bounds), PlacedObjectCategory.Furniture, bounds, categories);

        // Items (have bounds, all categorized as Item)
        Process(records.Weapons, w => (w.FormId, w.Bounds), PlacedObjectCategory.Item, bounds, categories);
        Process(records.Armor, a => (a.FormId, a.Bounds), PlacedObjectCategory.Item, bounds, categories);
        Process(records.Ammo, a => (a.FormId, a.Bounds), PlacedObjectCategory.Item, bounds, categories);
        Process(records.Consumables, c => (c.FormId, c.Bounds), PlacedObjectCategory.Item, bounds, categories);
        Process(records.MiscItems, m => (m.FormId, m.Bounds), PlacedObjectCategory.Item, bounds, categories);
        Process(records.Books, b => (b.FormId, b.Bounds), PlacedObjectCategory.Item, bounds, categories);

        // Promote statics with tree/plant model paths to Plant category
        foreach (var s in records.Statics)
        {
            if (s.ModelPath != null && s.ModelPath.Contains("trees", StringComparison.OrdinalIgnoreCase))
            {
                categories[s.FormId] = PlacedObjectCategory.Plant;
            }
        }

        // Category-only (no bounds data)
        foreach (var r in records.Npcs) { categories.TryAdd(r.FormId, PlacedObjectCategory.Npc); }
        foreach (var r in records.Creatures) { categories.TryAdd(r.FormId, PlacedObjectCategory.Creature); }
        foreach (var r in records.Containers) { categories.TryAdd(r.FormId, PlacedObjectCategory.Container); }

        return (bounds, categories);
    }

    private static void Process<T>(
        List<T> records,
        Func<T, (uint FormId, ObjectBounds? Bounds)> boundsSelector,
        PlacedObjectCategory category,
        Dictionary<uint, ObjectBounds> boundsIndex,
        Dictionary<uint, PlacedObjectCategory> categoryIndex)
    {
        foreach (var record in records)
        {
            var (formId, b) = boundsSelector(record);
            if (formId != 0)
            {
                if (b != null)
                {
                    boundsIndex.TryAdd(formId, b);
                }

                categoryIndex.TryAdd(formId, category);
            }
        }
    }
}
