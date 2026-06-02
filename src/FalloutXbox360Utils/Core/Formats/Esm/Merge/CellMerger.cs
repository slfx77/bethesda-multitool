using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Merge;

/// <summary>
///     Cell-merge mode chosen for a single cell based on the runtime DMP snapshot's contents.
///     Drives whether temporary children are merged into the master cell or treated as an
///     authoritative replacement snapshot.
///     Binary policy: persistent-only DMP captures merge into the master cell, anything with
///     non-persistent content authoritatively replaces it. Persistent vs non-persistent is the
///     load-bearing distinction — the DMP only carries non-persistent geometry for a cell when
///     the engine had that cell streamed in, which makes the snapshot authoritative for
///     temporary content. Persistent-only captures (map markers, persistent NPCs) say nothing
///     about temporaries either way.
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
    ///     The DMP cell carries at least one non-persistent reference, so the runtime snapshot
    ///     is treated as authoritative for ordinary temporary geometry and clutter. Persistent
    ///     master refs are still preserved (repositioned via override when DMP also captured
    ///     them); non-persistent master refs not in the DMP snapshot are delete-marked unless
    ///     <c>CellStructuralReferencePreserver.ShouldPreserveInLoadedReplacement</c> keeps
    ///     them (scripted refs, structural markers).
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
    ///     Classify the merge mode for a single DMP cell. Any non-persistent overrideable ref
    ///     flips the cell to <see cref="CellMergeMode.LoadedReplacement" />; otherwise persistent
    ///     refs alone give <see cref="CellMergeMode.PersistentOnly" />. Returns
    ///     <see cref="CellMergeMode.Skip" /> when none of the DMP's placed objects match a PC
    ///     ESM ref FormID.
    /// </summary>
    public static CellMergeMode Classify(
        CellRecord dmpCell,
        IReadOnlySet<uint> pcEsmRefFormIds)
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
            return CellMergeMode.LoadedReplacement;
        }

        return hasOverrideablePersistent ? CellMergeMode.PersistentOnly : CellMergeMode.Skip;
    }

    /// <summary>
    ///     Returns the placed refs that should be emitted as overrides for the chosen mode:
    ///     <see cref="CellMergeMode.PersistentOnly" /> emits only persistent refs (and map
    ///     markers, which behave as persistent for routing); <see cref="CellMergeMode.LoadedReplacement" />
    ///     emits both. Refs without a matching PC ESM FormID are filtered out — v2 does not
    ///     emit new refs.
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

            if (mode == CellMergeMode.PersistentOnly && !placed.IsPersistent && !placed.IsMapMarker)
            {
                continue;
            }

            yield return placed;
        }
    }
}
