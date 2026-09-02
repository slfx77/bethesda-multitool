using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>
///     Builds object bounds and model path indexes from parsed base-object records. The indexes are
///     built BEFORE cell parsing and handed to <c>CellLinkageHandler.ToPlacedReference</c> via
///     <c>RecordParserContext</c>, so each <c>PlacedReference</c> is BORN with its Bounds/ModelPath —
///     the previous post-parse enrichment sweep <c>with</c>-cloned essentially every placed ref
///     (5.1M clones on Fallout 76). A post-pass remains only for DMP loads, whose runtime merge can
///     append refs the construction-time path never saw.
/// </summary>
internal static class ObjectIndexBuilder
{
    /// <summary>Build bounds/model indexes from all parsed base-object record types.</summary>
    public static Dictionary<uint, ObjectBounds> BuildIndexes(
        List<StaticRecord> statics,
        List<ActivatorRecord> activators,
        List<DoorRecord> doors,
        List<LightRecord> lights,
        List<FurnitureRecord> furniture,
        List<StaticCollectionRecord> staticCollections,
        List<PlaceableWaterRecord> placeableWaters,
        List<BendableSplineRecord> bendableSplines,
        List<TreeRecord> trees,
        List<WeaponRecord> weapons,
        List<ArmorRecord> armor,
        List<AmmoRecord> ammo,
        List<ConsumableRecord> consumables,
        List<MiscItemRecord> miscItems,
        List<BookRecord> books,
        List<ContainerRecord> containers,
        List<KeyRecord> keys,
        List<NoteRecord> notes,
        List<WeaponModRecord> weaponMods,
        List<SoundRecord> sounds,
        List<GenericEsmRecord> genericRecords,
        Dictionary<uint, string> modelIndex)
    {
        var boundsIndex = new Dictionary<uint, ObjectBounds>();
        AddToIndexes(statics, s => s.FormId, s => s.Bounds, s => s.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(activators, a => a.FormId, a => a.Bounds, a => a.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(doors, d => d.FormId, d => d.Bounds, d => d.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(lights, l => l.FormId, l => l.Bounds, l => l.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(furniture, f => f.FormId, f => f.Bounds, f => f.ModelPath, boundsIndex, modelIndex);
        // SCOL (static collection, e.g. SSHQExterior03) — a placed ref to a SCOL resolves to its
        // merged meshes\scol\*.nif. Without this entry SCOL refs get no ModelPath and never render.
        AddToIndexes(staticCollections, s => s.FormId, s => s.Bounds, s => s.ModelPath, boundsIndex, modelIndex);
        // PWAT (placeable water, e.g. NVCleanWater1x402) — the water planes that sit in ponds, sewers
        // and craters, whose surface is NOT the cell's XCLW plane. PWAT used to ride the generic-record
        // list; once it moved to the typed ParsePlaceableWaters() path it stopped reaching this index,
        // so every placed water plane lost its MODL and the renderer dropped it. Same failure the SCOL
        // line above exists to prevent.
        AddToIndexes(placeableWaters, p => p.FormId, p => p.Bounds, p => p.ModelPath, boundsIndex, modelIndex);
        // BNDS has bounds but intentionally no model path: its REFR is procedural XBSD geometry.
        AddToIndexes(bendableSplines, b => b.FormId, b => b.Bounds, _ => null, boundsIndex, modelIndex);
        // TREE moved out of GenericRecords 2026-08-07. Without this line every tree
        // placement loses its .spt model path — the exact regression the PWAT note
        // above records, repeated.
        AddToIndexes(trees, t => t.FormId, t => t.Bounds, t => t.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(weapons, w => w.FormId, w => w.Bounds, w => w.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(armor, a => a.FormId, a => a.Bounds, a => a.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(ammo, a => a.FormId, a => a.Bounds, a => a.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(consumables, c => c.FormId, c => c.Bounds, c => c.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(miscItems, m => m.FormId, m => m.Bounds, m => m.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(books, b => b.FormId, b => b.Bounds, b => b.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(containers, c => c.FormId, c => null, c => c.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(keys, k => k.FormId, k => null, k => k.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(notes, n => n.FormId, n => null, n => n.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(weaponMods, w => w.FormId, w => null, w => w.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(sounds, s => s.FormId, s => s.Bounds, s => null, boundsIndex, modelIndex);
        AddToIndexes(genericRecords, g => g.FormId, g => g.Bounds, g => g.ModelPath, boundsIndex, modelIndex);

        return boundsIndex;
    }

    /// <summary>
    ///     DMP-only post-pass: enrich every cell view with the indexes. Construction-time enrichment
    ///     covers all refs built through <c>ToPlacedReference</c>; this catches runtime-merged
    ///     placements added outside that path. Enrichment mutates in place, and worldspace cell lists
    ///     alias the SAME <c>CellRecord</c> instances as the top-level list (LinkCellsToWorldspaces),
    ///     so only worldspace cells NOT aliased into the top-level list (runtime-cell-map worldspaces)
    ///     need a second sweep — re-enriching an aliased cell would just re-clone its refs.
    /// </summary>
    public static void EnrichAllCellViews(
        List<CellRecord> cells,
        List<WorldspaceRecord> worldspaces,
        Dictionary<uint, ObjectBounds> boundsIndex,
        Dictionary<uint, string> modelIndex)
    {
        WorldRecordHandler.EnrichPlacedReferences(cells, boundsIndex, modelIndex);

        var aliased = new HashSet<CellRecord>(ReferenceEqualityComparer.Instance);
        foreach (var cell in cells)
        {
            aliased.Add(cell);
        }

        List<CellRecord>? unaliased = null;
        foreach (var ws in worldspaces)
        {
            foreach (var cell in ws.Cells)
            {
                if (!aliased.Contains(cell))
                {
                    (unaliased ??= []).Add(cell);
                }
            }
        }

        if (unaliased is not null)
        {
            WorldRecordHandler.EnrichPlacedReferences(unaliased, boundsIndex, modelIndex);
        }
    }

    private static void AddToIndexes<T>(
        List<T> records,
        Func<T, uint> formIdSelector,
        Func<T, ObjectBounds?> boundsSelector,
        Func<T, string?> modelSelector,
        Dictionary<uint, ObjectBounds> boundsIndex,
        Dictionary<uint, string> modelIndex)
    {
        foreach (var record in records)
        {
            var formId = formIdSelector(record);
            if (formId == 0)
            {
                continue;
            }

            var bounds = boundsSelector(record);
            if (bounds != null)
            {
                boundsIndex.TryAdd(formId, bounds);
            }

            var model = modelSelector(record);
            if (model != null)
            {
                modelIndex.TryAdd(formId, model);
            }
        }
    }
}
