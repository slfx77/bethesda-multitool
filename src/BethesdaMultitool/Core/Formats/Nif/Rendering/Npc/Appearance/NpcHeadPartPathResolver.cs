using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;

/// <summary>Resolves the mesh/texture paths for an NPC's head parts (HDPT records) by FormID.</summary>
internal sealed class NpcHeadPartPathResolver
{
    private readonly IReadOnlyDictionary<uint, HdptScanEntry> _headParts;

    internal NpcHeadPartPathResolver(
        IReadOnlyDictionary<uint, HdptScanEntry> headParts)
    {
        _headParts = headParts;
    }

    internal List<string>? Resolve(List<uint>? headPartFormIds)
    {
        if (headPartFormIds is not { Count: > 0 })
        {
            return null;
        }

        var paths = new List<string>();
        foreach (var headPartFormId in headPartFormIds)
        {
            if (_headParts.TryGetValue(headPartFormId, out var headPart) &&
                headPart.ModelPath != null)
            {
                var meshPath = NpcAppearancePathDeriver.AsMeshPath(headPart.ModelPath);
                if (meshPath != null)
                {
                    paths.Add(meshPath);
                }
            }
        }

        return paths.Count > 0 ? paths : null;
    }
}

