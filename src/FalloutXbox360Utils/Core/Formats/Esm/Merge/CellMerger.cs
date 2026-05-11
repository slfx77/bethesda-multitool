using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Merge;

/// <summary>
///     Cell-merge mode chosen for a single cell based on the runtime DMP snapshot's contents.
///     Drives whether temporary children (objects) are overridden alongside persistent refs.
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
    ///     The DMP cell carries at least one temporary (non-persistent) ref — typical of
    ///     cells the player was actively in. We override both persistent and temporary refs.
    ///     v2 does NOT delete refs that exist in the PC ESM but not in the DMP — that
    ///     "wipeout" semantics needs the deleted-flag override path which is deferred to v3.
    /// </summary>
    HasTemporary,

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
    public static CellMergeMode Classify(CellRecord dmpCell, IReadOnlySet<uint> pcEsmRefFormIds)
    {
        var hasOverrideablePersistent = false;
        var hasOverrideableTemporary = false;

        foreach (var placed in dmpCell.PlacedObjects)
        {
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

        if (hasOverrideableTemporary)
        {
            return CellMergeMode.HasTemporary;
        }

        return hasOverrideablePersistent ? CellMergeMode.PersistentOnly : CellMergeMode.Skip;
    }

    /// <summary>
    ///     Returns the placed refs that should be emitted as overrides for the chosen mode:
    ///     <see cref="CellMergeMode.PersistentOnly" /> emits only persistent refs;
    ///     <see cref="CellMergeMode.HasTemporary" /> emits both. Refs without a matching PC ESM
    ///     FormID are filtered out — v2 does not emit new refs.
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
