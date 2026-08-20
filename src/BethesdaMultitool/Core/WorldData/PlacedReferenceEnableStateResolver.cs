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
    /// <summary>Convenience wrapper for callers without a prebuilt index (tests, small worlds).</summary>
    internal static HashSet<uint> ResolveXespDisabledRefs(IReadOnlyList<CellRecord> cells) =>
        ResolveXespDisabledRefs(PlacedRefIndex.Build(cells));

    /// <summary>
    ///     Index-driven resolve: the shared <see cref="PlacedRefIndex" /> replaces the private
    ///     full-population byId dictionary this used to build per call (5.1M entries on FO76).
    ///     Duplicate placement FormIDs collapse first-wins in the index; the result keys by FormID,
    ///     so a duplicate could never be distinguished downstream anyway.
    /// </summary>
    internal static HashSet<uint> ResolveXespDisabledRefs(PlacedRefIndex placedRefs)
    {
        var result = new HashSet<uint>();
        foreach (var entry in placedRefs.Entries)
        {
            var placement = entry.Ref;
            if (placement.EnableParentFormId is > 0 &&
                !placement.IsInitiallyDisabled &&
                ResolveDisabled(placement, placedRefs))
            {
                result.Add(placement.FormId);
            }
        }

        return result;
    }

    private static bool ResolveDisabled(
        PlacedReference placement,
        PlacedRefIndex placedRefs)
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
                !placedRefs.TryGetRef(parentId, out var parent))
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
