using System.Collections.Frozen;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>A snapshot of the selected WRLD water component; stored records remain unchanged.</summary>
internal sealed record WorldWaterSelection(uint? ContextFormId, uint? SourceFormId,
    float? Height, uint? WaterFormId, bool RequiresCellHasWater, string Status, ImmutableArray<uint> Path)
{
    internal static WorldWaterSelection Unavailable(uint? context = null) =>
        new(context, null, null, null, false, "unavailable", context is { } id ? [id] : []);
}

/// <summary>Immutable per-load-order water routes, shared by reports, maps and lazy cell caches.</summary>
internal sealed class WorldWaterCatalog
{
    private readonly FrozenDictionary<uint, WorldWaterSelection> _worlds;

    private WorldWaterCatalog(Dictionary<uint, WorldWaterSelection> worlds) => _worlds = worlds.ToFrozenDictionary();

    internal static WorldWaterCatalog Create(IReadOnlyList<WorldspaceRecord> worlds, BethesdaGame game)
    {
        var selections = new Dictionary<uint, WorldWaterSelection>();
        foreach (var group in worlds.GroupBy(w => w.FormId))
        {
            if (group.Skip(1).Any())
            {
                selections[group.Key] = WorldWaterSelection.Unavailable(group.Key) with { Status = "ambiguous-world" };
                continue;
            }
            var world = group.First();
            var route = game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas
                ? WorldspaceInheritanceResolver.Resolve(world, worlds, WorldspaceComponent.Water)
                : new WorldspaceComponentRoute(world, [world.FormId],
                    world.WaterFromParentWorldspace ? "parser-derived" : "direct");
            selections[world.FormId] = new(world.FormId, route.Status == "parser-derived" ? null : route.Source?.FormId,
                route.Source?.DefaultWaterHeight, route.Source?.WaterFormId,
                game == BethesdaGame.Oblivion && world.WaterFromParentWorldspace,
                route.Status, [.. route.Path]);
        }
        return new(selections);
    }

    internal WorldWaterSelection Get(uint? worldFormId) => worldFormId is { } id && _worlds.TryGetValue(id, out var found)
        ? found : WorldWaterSelection.Unavailable(worldFormId);

    internal float? ResolveHeight(CellRecord cell, float? unlinkedDefault = null, bool unlinkedRequiresWater = false)
    {
        if (cell.IsInterior) return WorldRenderCache.ResolveEffectiveWaterHeight(cell, null);
        if (cell.WorldspaceFormId is { } id)
        {
            var selected = Get(id);
            return WorldRenderCache.ResolveEffectiveWaterHeight(cell, selected.Height, selected.RequiresCellHasWater);
        }
        return WorldRenderCache.ResolveEffectiveWaterHeight(cell, unlinkedDefault, unlinkedRequiresWater);
    }
}
