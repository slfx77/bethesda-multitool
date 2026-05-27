using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Merge;

/// <summary>
///     Cell-merge mode chosen for a single cell based on the runtime DMP snapshot's contents.
///     Drives whether temporary children are merged into the master cell or treated as an
///     authoritative replacement snapshot.
/// </summary>
public enum CellMergeMode
{
    /// <summary>
    ///     The DMP cell carries only persistent references — typical of cells that were not
    ///     actively loaded at dump time. We override the persistent refs and leave the cell's
    ///     temporary children untouched in the source ESM.
    /// </summary>
    PersistentOnly,

    /// <summary>
    ///     The DMP cell carries temporary refs, but not enough static/layout evidence to
    ///     prove the full cell was loaded. Merge the captured refs into the master cell and
    ///     preserve missing master children.
    /// </summary>
    HasTemporary,

    /// <summary>
    ///     The DMP cell carries non-persistent static/layout placement evidence, so the
    ///     runtime snapshot is authoritative for ordinary temporary geometry and clutter.
    ///     Missing master children are not copied wholesale; only script-critical and
    ///     structural refs are retained.
    /// </summary>
    LoadedReplacement,

    /// <summary>
    ///     The DMP cell has no overrideable refs (either it's empty in the runtime snapshot
    ///     or none of its refs match a PC ESM FormID). The orchestrator skips the cell entirely.
    /// </summary>
    Skip
}

/// <summary>
///     Classifies a runtime cell into a merge mode based on the placed objects it carries
///     and the master ESM ownership of those FormIDs.
/// </summary>
public static class CellMerger
{
    /// <summary>
    ///     Classify the merge mode for a single DMP cell. Returns <see cref="CellMergeMode.Skip" />
    ///     when none of the DMP's placed objects match a PC ESM ref FormID.
    /// </summary>
    public static CellMergeMode Classify(
        CellRecord dmpCell,
        IReadOnlySet<uint> pcEsmRefFormIds,
        Func<PlacedReference, bool>? isLoadedPlacement = null,
        int loadedPlacementThreshold = 1)
    {
        var hasOverrideablePersistent = false;
        var hasOverrideableTemporary = false;
        var loadedPlacementCount = 0;
        loadedPlacementThreshold = Math.Max(1, loadedPlacementThreshold);

        foreach (var placed in dmpCell.PlacedObjects)
        {
            if (!placed.IsPersistent
                && isLoadedPlacement?.Invoke(placed) == true)
            {
                loadedPlacementCount++;
            }

            if (!pcEsmRefFormIds.Contains(placed.FormId))
            {
                continue;
            }

            if (placed.IsPersistent)
            {
                hasOverrideablePersistent = true;
            }
            else
            {
                hasOverrideableTemporary = true;
            }
        }

        if (loadedPlacementCount >= loadedPlacementThreshold)
        {
            return CellMergeMode.LoadedReplacement;
        }

        if (hasOverrideableTemporary)
        {
            return CellMergeMode.HasTemporary;
        }

        return hasOverrideablePersistent ? CellMergeMode.PersistentOnly : CellMergeMode.Skip;
    }

    /// <summary>
    ///     Returns the placed refs that should be emitted as overrides for the chosen mode:
    ///     <see cref="CellMergeMode.PersistentOnly" /> emits only persistent refs;
    ///     <see cref="CellMergeMode.HasTemporary" /> and
    ///     <see cref="CellMergeMode.LoadedReplacement" /> emit both. Refs without a matching
    ///     PC ESM FormID are filtered out — v2 does not emit new refs.
    /// </summary>
    public static IEnumerable<PlacedReference> SelectOverrideRefs(
        CellRecord dmpCell,
        CellMergeMode mode,
        IReadOnlySet<uint> pcEsmRefFormIds)
    {
        if (mode == CellMergeMode.Skip)
        {
            yield break;
        }

        foreach (var placed in dmpCell.PlacedObjects)
        {
            if (!pcEsmRefFormIds.Contains(placed.FormId))
            {
                continue;
            }

            if (mode == CellMergeMode.PersistentOnly && !placed.IsPersistent)
            {
                continue;
            }

            yield return placed;
        }
    }
}
