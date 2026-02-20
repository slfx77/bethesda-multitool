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

        // Items without OBND (category-only)
        foreach (var r in records.Keys)
        {
            if (r.FormId != 0)
            {
                categories.TryAdd(r.FormId, PlacedObjectCategory.Item);
            }
        }

        foreach (var r in records.Notes)
        {
            if (r.FormId != 0)
            {
                categories.TryAdd(r.FormId, PlacedObjectCategory.Item);
            }
        }

        foreach (var r in records.WeaponMods)
        {
            if (r.FormId != 0)
            {
                categories.TryAdd(r.FormId, PlacedObjectCategory.Item);
            }
        }

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

        foreach (var r in records.Terminals)
        {
            categories.TryAdd(r.FormId, PlacedObjectCategory.Activator);
        }

        foreach (var r in records.Sounds)
        {
            if (r.FormId != 0)
            {
                if (r.Bounds != null)
                {
                    bounds.TryAdd(r.FormId, r.Bounds);
                }

                categories.TryAdd(r.FormId, PlacedObjectCategory.Sound);
            }
        }

        foreach (var r in records.TextureSets)
        {
            if (r.FormId != 0)
            {
                if (r.Bounds != null)
                {
                    bounds.TryAdd(r.FormId, r.Bounds);
                }

                categories.TryAdd(r.FormId, PlacedObjectCategory.Effects);
            }
        }

        // Leveled lists: LVLN → Npc, LVLC → Creature, LVLI → Item
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
            else if (ll.ListType == "LVLI")
            {
                categories.TryAdd(ll.FormId, PlacedObjectCategory.Item);
            }
        }

        // Generic records: type-specific categorization
        foreach (var gr in records.GenericRecords)
        {
            if (gr.FormId == 0)
            {
                continue;
            }

            if (gr.Bounds != null)
            {
                bounds.TryAdd(gr.FormId, gr.Bounds);
            }

            var genericCategory = gr.RecordType switch
            {
                "MSTT" => PlacedObjectCategory.Static,
                "TACT" => PlacedObjectCategory.Activator,
                "TREE" => PlacedObjectCategory.Landscape,
                "ADDN" => PlacedObjectCategory.Effects,
                "CAMS" => PlacedObjectCategory.Effects,
                "ANIO" => PlacedObjectCategory.Effects,
                "IPDS" => PlacedObjectCategory.Effects,
                "EFSH" => PlacedObjectCategory.Effects,
                "RGDL" => PlacedObjectCategory.Effects,
                "LSCR" => PlacedObjectCategory.Static,
                "ASPC" => PlacedObjectCategory.Sound,
                "MSET" => PlacedObjectCategory.Sound,
                "CHIP" => PlacedObjectCategory.Item,
                "CSNO" => PlacedObjectCategory.Activator,
                "DOBJ" => PlacedObjectCategory.Static,
                "IMAD" => PlacedObjectCategory.Effects,
                "IDLM" => PlacedObjectCategory.Effects,
                "SCOL" => PlacedObjectCategory.Static,
                "PWAT" => PlacedObjectCategory.Landscape,
                _ => (PlacedObjectCategory?)null
            };

            if (genericCategory.HasValue)
            {
                categories.TryAdd(gr.FormId, genericCategory.Value);
            }
        }

        // Promote MSTT (Moveable Static) generic records with known GECK folder categories
        foreach (var gr in records.GenericRecords)
        {
            if (gr.RecordType == "MSTT" && gr.ModelPath != null)
            {
                var folderCategory = GetStaticCategoryFromModelPath(gr.ModelPath);
                if (folderCategory.HasValue)
                {
                    categories[gr.FormId] = folderCategory.Value;
                }
            }
        }

        // Hardcoded engine FormIDs (not present as explicit records in ESM)
        // These are engine marker statics (e.g., CylinderMarkerXLarge) used for collision/triggers
        categories.TryAdd(0x00000017, PlacedObjectCategory.Effects);
        categories.TryAdd(0x00000020, PlacedObjectCategory.Effects);

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

        // Strip named DLC folder prefixes (FO3 assets reused in FNV)
        path = StripNamedDlcPrefix(path);

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

        if (folder.Equals("furniture", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Furniture;
        }

        if (folder.Equals("markers", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("marker", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Effects;
        }

        if (folder.Equals("weapons", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Item;
        }

        if (folder.Equals("armor", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Item;
        }

        if (folder.Equals("creatures", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Creature;
        }

        if (folder.Equals("characters", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Npc;
        }

        if (folder.Equals("lights", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Light;
        }

        if (folder.Equals("animobjects", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Effects;
        }

        if (folder.Equals("water", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Landscape;
        }

        if (folder.Equals("terminals", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Activator;
        }

        if (folder.Equals("gore", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Effects;
        }

        if (folder.Equals("sky", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Landscape;
        }

        if (folder.Equals("scol", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Static;
        }

        if (folder.Equals("interface", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Effects;
        }

        return null;
    }

    /// <summary>
    ///     Strips named DLC folder prefixes from model paths.
    ///     Any first folder segment starting with "dlc" (case insensitive) is treated as a DLC
    ///     content prefix and stripped. Handles FO3 assets reused in FNV (dlcanch\, DLCPitt\, etc.).
    /// </summary>
    private static ReadOnlySpan<char> StripNamedDlcPrefix(ReadOnlySpan<char> path)
    {
        if (path.Length < 5 || !path[..3].Equals("dlc", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var sepIndex = path.IndexOfAny('\\', '/');
        if (sepIndex > 3)
        {
            return path[(sepIndex + 1)..];
        }

        return path;
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
