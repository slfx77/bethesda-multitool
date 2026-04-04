using System.Diagnostics;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Builds object bounds and model path indexes from parsed records, then enriches
///     placed references (REFR/ACHR/ACRE) with base object data.
///     Extracted from RecordParser.ParseAll.
/// </summary>
internal static class ObjectIndexBuilder
{
    /// <summary>
    ///     Build bounds/model indexes from all parsed record types and enrich placed references.
    /// </summary>
    public static void BuildAndEnrich(
        List<StaticRecord> statics,
        List<ActivatorRecord> activators,
        List<DoorRecord> doors,
        List<LightRecord> lights,
        List<FurnitureRecord> furniture,
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
        List<CellRecord> cells,
        List<WorldspaceRecord> worldspaces,
        Dictionary<uint, string> modelIndex,
        Stopwatch phaseSw)
    {
        phaseSw.Restart();
        var boundsIndex = new Dictionary<uint, ObjectBounds>();
        AddToIndexes(statics, s => s.FormId, s => s.Bounds, s => s.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(activators, a => a.FormId, a => a.Bounds, a => a.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(doors, d => d.FormId, d => d.Bounds, d => d.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(lights, l => l.FormId, l => l.Bounds, l => l.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(furniture, f => f.FormId, f => f.Bounds, f => f.ModelPath, boundsIndex, modelIndex);
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

        WorldRecordHandler.EnrichPlacedReferences(cells, boundsIndex, modelIndex);
        foreach (var ws in worldspaces)
        {
            WorldRecordHandler.EnrichPlacedReferences(ws.Cells, boundsIndex, modelIndex);
        }

        Logger.Instance.Debug(
            $"  [Semantic] Enrichment: {phaseSw.Elapsed} (Bounds: {boundsIndex.Count}, Models: {modelIndex.Count})");
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
