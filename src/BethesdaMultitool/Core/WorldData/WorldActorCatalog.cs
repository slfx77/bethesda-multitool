using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>Selected actor bases; no asset payloads or per-placement geometry are retained here.</summary>
internal sealed class WorldActorCatalog
{
    private readonly Dictionary<(string Type, uint Id), WorldActorDefinition> _actors = [];

    internal WorldActorCatalog(NpcAppearanceResolver resolver)
    {
        Resolver = resolver;
        foreach (var id in resolver.GetAllNpcs().Keys) Add("ACHR", "NPC_", id);
        foreach (var id in resolver.GetAllCreatures().Keys) Add("ACRE", "CREA", id);

        void Add(string placementType, string baseType, uint id)
        {
            // Only selected physical winners have a source. Do not turn a donor/runtime fallback
            // or an ambiguous/deleted record into a stored-world appearance.
            if (resolver.Sources.TryGetValue(id, out var source) && source.Signature == baseType)
                _actors[(placementType, id)] = new WorldActorDefinition(placementType, id,
                    $"meshes\\__bmt_world_actor\\{placementType}\\{id:X8}.nif", source);
        }
    }

    internal NpcAppearanceResolver Resolver { get; }
    internal IReadOnlyCollection<WorldActorDefinition> Actors => _actors.Values;

    internal WorldActorDefinition? Resolve(PlacedReference placement) =>
        _actors.GetValueOrDefault((placement.RecordType, placement.BaseFormId));
}

internal sealed record WorldActorDefinition(
    string PlacementType,
    uint BaseFormId,
    string CacheKey,
    LoadOrderRecordVersion Source);
