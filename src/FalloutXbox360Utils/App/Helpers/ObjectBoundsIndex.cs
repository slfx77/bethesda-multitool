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

        // Promote statics with known GECK folder categories
        foreach (var s in records.Statics)
        {
            if (s.ModelPath != null)
            {
                var folderCategory = GetStaticCategoryFromModelPath(s.ModelPath);
                if (folderCategory.HasValue)
                {
                    categories[s.FormId] = folderCategory.Value;
                }
            }
        }

        // Category-only (no bounds data)
        foreach (var r in records.Npcs)
        {
            categories.TryAdd(r.FormId, PlacedObjectCategory.Npc);
        }

        foreach (var r in records.Creatures)
        {
            categories.TryAdd(r.FormId, PlacedObjectCategory.Creature);
        }

        foreach (var r in records.Containers)
        {
            categories.TryAdd(r.FormId, PlacedObjectCategory.Container);
        }

        // Leveled lists: LVLN → Npc, LVLC → Creature
        foreach (var ll in records.LeveledLists)
        {
            if (ll.ListType == "LVLN")
            {
                categories.TryAdd(ll.FormId, PlacedObjectCategory.Npc);
            }
            else if (ll.ListType == "LVLC")
            {
                categories.TryAdd(ll.FormId, PlacedObjectCategory.Creature);
            }
        }

        return (bounds, categories);
    }

    /// <summary>
    ///     Determines a PlacedObjectCategory from the GECK model path top-level folder.
    ///     Strips the meshes\ prefix and any DLC subdirectory prefix (DLC01\, DLC02\, etc.).
    /// </summary>
    internal static PlacedObjectCategory? GetStaticCategoryFromModelPath(string modelPath)
    {
        var path = modelPath.AsSpan();

        // Strip "meshes\" or "meshes/" prefix
        if (path.Length > 7 &&
            path[..7].Equals("meshes\\", StringComparison.OrdinalIgnoreCase) ||
            path[..7].Equals("meshes/", StringComparison.OrdinalIgnoreCase))
        {
            path = path[7..];
        }

        // Strip DLC directory prefix (e.g., "DLC01\", "DLC02\", "DLC03\", "DLC04\")
        if (path.Length > 6 &&
            path[..3].Equals("dlc", StringComparison.OrdinalIgnoreCase) &&
            path[3] >= '0' && path[3] <= '9' &&
            path[4] >= '0' && path[4] <= '9' &&
            (path[5] == '\\' || path[5] == '/'))
        {
            path = path[6..];
        }

        // Find the first path segment
        var sepIndex = path.IndexOfAny('\\', '/');
        if (sepIndex <= 0)
        {
            return null;
        }

        var folder = path[..sepIndex];

        // Match against GECK folder categories
        if (folder.Equals("architecture", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Architecture;
        }

        if (folder.Equals("landscape", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("rocks", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("trees", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("plants", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("shrubs", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("flowers", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("cactus", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("grass", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("bushes", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("tumbleweed", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Landscape;
        }

        if (folder.Equals("clutter", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Clutter;
        }

        if (folder.Equals("dungeon", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("dungeons", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Dungeon;
        }

        if (folder.Equals("effects", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("decals", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Effects;
        }

        if (folder.Equals("vehicles", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Vehicles;
        }

        if (folder.Equals("traps", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Traps;
        }

        return null;
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
