using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool;

/// <summary>
///     Resolves authored XESP enable-parent chains without UI or renderer state. The returned set
///     contains children whose own REFR flag is enabled but whose initial-world parent chain makes
///     them disabled; callers combine it with each placement's own Initially Disabled flag.
/// </summary>
internal static class PlacedReferenceEnableStateResolver
{
    internal static HashSet<uint> ResolveXespDisabledRefs(IReadOnlyList<CellRecord> cells)
    {
        var byId = new Dictionary<uint, PlacedReference>();
        var linked = new List<PlacedReference>();
        foreach (var cell in cells)
        {
            foreach (var placement in cell.PlacedObjects)
            {
                byId.TryAdd(placement.FormId, placement);
                if (placement.EnableParentFormId is > 0)
                {
                    linked.Add(placement);
                }
            }
        }

        var result = new HashSet<uint>();
        foreach (var placement in linked)
        {
            if (!placement.IsInitiallyDisabled && ResolveDisabled(placement, byId, depth: 0))
            {
                result.Add(placement.FormId);
            }
        }

        return result;
    }

    private static bool ResolveDisabled(
        PlacedReference placement,
        IReadOnlyDictionary<uint, PlacedReference> byId,
        int depth)
    {
        if (depth >= 16 || placement.EnableParentFormId is not { } parentId || parentId == 0 ||
            !byId.TryGetValue(parentId, out var parent))
        {
            return placement.IsInitiallyDisabled;
        }

        var parentDisabled = ResolveDisabled(parent, byId, depth + 1);
        var opposite = ((placement.EnableParentFlags ?? 0) & 0x01) != 0;
        return opposite ? !parentDisabled : parentDisabled;
    }
}
