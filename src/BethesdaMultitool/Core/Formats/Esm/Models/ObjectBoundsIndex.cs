namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>
///     Builds a FormID to ObjectBounds lookup from all record types that have OBND.
///     Used by the World tab to draw bounding rectangles for placed objects.
/// </summary>
internal static class ObjectBoundsIndex
{
    /// <summary>Builds a FormID to <see cref="ObjectBounds" /> lookup from every record type that carries an OBND.</summary>
    public static Dictionary<uint, ObjectBounds> Build(RecordCollection records)
    {
        var (bounds, _) = BuildCombined(records);
        return bounds;
    }

    /// <summary>
    ///     Builds a FormID → MODL model-path lookup from every base record type that carries a model
    ///     (mirrors <see cref="Parsing.ObjectIndexBuilder" />). Used by the World tab's selected-object
    ///     pane to surface a placed reference's NIF/SPT path when the reference wasn't directly enriched.
    /// </summary>
    public static Dictionary<uint, string> BuildModelPathIndex(RecordCollection records)
    {
        var models = new Dictionary<uint, string>();
        AddModels(records.Statics, s => s.FormId, s => s.ModelPath, models);
        AddModels(records.Activators, a => a.FormId, a => a.ModelPath, models);
        AddModels(records.Doors, d => d.FormId, d => d.ModelPath, models);
        AddModels(records.Lights, l => l.FormId, l => l.ModelPath, models);
        AddModels(records.Furniture, f => f.FormId, f => f.ModelPath, models);
        AddModels(records.StaticCollections, s => s.FormId, s => s.ModelPath, models);
        AddModels(records.Weapons, w => w.FormId, w => w.ModelPath, models);
        AddModels(records.Armor, a => a.FormId, a => a.ModelPath, models);
        AddModels(records.Ammo, a => a.FormId, a => a.ModelPath, models);
        AddModels(records.Consumables, c => c.FormId, c => c.ModelPath, models);
        AddModels(records.MiscItems, m => m.FormId, m => m.ModelPath, models);
        AddModels(records.Books, b => b.FormId, b => b.ModelPath, models);
        AddModels(records.Containers, c => c.FormId, c => c.ModelPath, models);
        AddModels(records.Keys, k => k.FormId, k => k.ModelPath, models);
        AddModels(records.Notes, n => n.FormId, n => n.ModelPath, models);
        AddModels(records.WeaponMods, w => w.FormId, w => w.ModelPath, models);
        AddModels(records.GenericRecords, g => g.FormId, g => g.ModelPath, models);
        return models;
    }

    private static void AddModels<T>(
        List<T> records,
        Func<T, uint> formIdSelector,
        Func<T, string?> modelSelector,
        Dictionary<uint, string> models)
    {
        foreach (var record in records)
        {
            var formId = formIdSelector(record);
            var model = modelSelector(record);
            if (formId != 0 && !string.IsNullOrEmpty(model))
            {
                models.TryAdd(formId, model);
            }
        }
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
                // TREE is the engine-authoritative tree identity: covers every Gamebryo .spt tree
                // (Oblivion/FO3/FNV MODLs are bare names like "\WastelandShrub01.spt" with no folder
                // segment, so path logic can never classify them) and TREE-record NIF trees in
                // Skyrim/FO4. Distinct from Plants so the viewer's Trees toggle is meaningful.
                "TREE" => PlacedObjectCategory.Tree,
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

                // Morrowind (TES3) routes every non-CELL/LAND/LTEX record into GenericRecords with its
                // raw 4-char code (TES4 parses these same codes into typed lists, so they never reach
                // here — these arms are TES3-only). Without them every placed TES3 object resolves to
                // Unknown and renders no map dot/box. NPC_/CREA become ACHR/ACRE refs upstream
                // (Tes3RecordParser.ReferenceRecordType), so they don't need an arm here.
                "STAT" => PlacedObjectCategory.Static,
                "ACTI" => PlacedObjectCategory.Activator,
                "DOOR" => PlacedObjectCategory.Door,
                "CONT" => PlacedObjectCategory.Container,
                "LIGH" => PlacedObjectCategory.Light,
                "FURN" => PlacedObjectCategory.Furniture,
                "WEAP" or "ARMO" or "MISC" or "BOOK" or "INGR" or "ALCH" or "APPA"
                    or "CLOT" or "REPA" or "LOCK" or "PROB" or "LEVI" => PlacedObjectCategory.Item,
                "LEVC" => PlacedObjectCategory.Creature,
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

        // Strip "meshes\" or "meshes/" prefix. The length guard must cover BOTH comparisons — a path
        // shorter than 7 chars (e.g. FO4's "sky\x.nif" placements, or any short model name) would throw
        // on path[..7] for the second Equals without it.
        if (path.Length > 7 &&
            (path[..7].Equals("meshes\\", StringComparison.OrdinalIgnoreCase) ||
             path[..7].Equals("meshes/", StringComparison.OrdinalIgnoreCase)))
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

        // Trees: match a whole "trees" segment ANYWHERE in the path, not just the first folder —
        // Skyrim/FO4/FO76 ship their STAT-based NIF trees under landscape\trees\, which first-segment
        // matching filed as Landscape (whole-segment, so architecture\treehouse.nif stays put).
        if (ContainsWholeSegment(path, "trees"))
        {
            return PlacedObjectCategory.Tree;
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
            folder.Equals("rocks", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Landscape;
        }

        if (folder.Equals("plants", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("shrubs", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("flowers", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("cactus", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("grass", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("bushes", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("tumbleweed", StringComparison.OrdinalIgnoreCase))
        {
            return PlacedObjectCategory.Plants;
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
            return PlacedObjectCategory.Sky;
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
    ///     True when the path contains <paramref name="segment" /> as a WHOLE path segment (bounded
    ///     by <c>\</c> / <c>/</c> or the string ends): <c>landscape\trees\x.nif</c> matches
    ///     <c>trees</c>; <c>architecture\treehouse.nif</c> does not.
    /// </summary>
    private static bool ContainsWholeSegment(ReadOnlySpan<char> path, string segment)
    {
        var start = 0;
        while (start <= path.Length - segment.Length)
        {
            var idx = path[start..].IndexOf(segment, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }
            var s = start + idx;
            var e = s + segment.Length;
            var boundedLeft = s == 0 || path[s - 1] == '\\' || path[s - 1] == '/';
            var boundedRight = e == path.Length || path[e] == '\\' || path[e] == '/';
            if (boundedLeft && boundedRight)
            {
                return true;
            }
            start = s + 1;
        }
        return false;
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
