using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.WorldData;

internal enum WorldspaceComponent { Water = 3, Climate = 4, ImageSpace = 5 }

internal sealed record WorldspaceComponentRoute(
    WorldspaceRecord? Source, IReadOnlyList<uint> Path, string Status);

/// <summary>Follows one PNAM component through retained WRLD records without modifying stored fields.</summary>
internal static class WorldspaceInheritanceResolver
{
    internal static WorldspaceComponentRoute Resolve(WorldspaceRecord? world,
        IReadOnlyList<WorldspaceRecord>? worlds, WorldspaceComponent component)
    {
        if (world is null) return new(null, [], "unavailable");
        var path = new List<uint>();
        var seen = new HashSet<uint>();
        var current = world;
        while (seen.Add(current.FormId))
        {
            path.Add(current.FormId);
            if ((current.ParentUseFlags.GetValueOrDefault() & (1 << (int)component)) == 0)
                return new(current, path, path.Count == 1 ? "direct" : "inherited");
            var parentId = current.ParentWorldspaceFormId.GetValueOrDefault();
            if (parentId == 0) return new(null, path, "missing-parent");
            WorldspaceRecord? parent = null;
            foreach (var candidate in worlds ?? [])
            {
                if (candidate.FormId != parentId) continue;
                if (parent is not null)
                {
                    path.Add(parentId);
                    return new(null, path, "ambiguous-parent");
                }
                parent = candidate;
            }
            if (parent is null)
            {
                path.Add(parentId);
                return new(null, path, "missing-parent");
            }
            current = parent;
        }
        path.Add(current.FormId);
        return new(null, path, "parent-cycle");
    }
}
