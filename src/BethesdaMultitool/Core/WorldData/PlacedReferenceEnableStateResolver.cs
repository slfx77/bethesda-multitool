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
            if (!placement.IsInitiallyDisabled && ResolveDisabled(placement, byId))
            {
                result.Add(placement.FormId);
            }
        }

        return result;
    }

    private static bool ResolveDisabled(
        PlacedReference placement,
        Dictionary<uint, PlacedReference> byId)
    {
        var visited = new HashSet<uint>();
        var invert = false;
        var current = placement;
        while (true)
        {
            // Treat malformed self/multi-node loops as disabled instead of allowing inverse-edge
            // parity or an arbitrary recursion depth to decide.
            if (!visited.Add(current.FormId))
            {
                return true;
            }

            if (current.EnableParentFormId is not { } parentId || parentId == 0 ||
                !byId.TryGetValue(parentId, out var parent))
            {
                return invert ? !current.IsInitiallyDisabled : current.IsInitiallyDisabled;
            }

            if (((current.EnableParentFlags ?? 0) & 0x01) != 0)
            {
                invert = !invert;
            }

            current = parent;
        }
    }
}
