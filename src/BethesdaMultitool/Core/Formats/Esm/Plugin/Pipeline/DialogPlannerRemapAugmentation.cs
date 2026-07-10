namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;

/// <summary>
///     Bridges the two-pass planner's new-record FormID allocations into the legacy dialogue
///     remap table that <c>DialogGrupBuilder</c> consumes.
/// </summary>
/// <remarks>
///     Under <c>--planner-types all</c> new proto records (QUST, NPC_, CREA, SPEL, …) are
///     emitted by <c>PlanWriter</c>, whose dispatch returns before the legacy
///     <c>TrackNewRecordSourceAlias</c> path — so their <c>source → emitted</c> mappings live
///     only in <c>EmitPlan.SourceToEmittedFormId</c>. <c>DialogGrupBuilder</c> resolves INFO/DIAL
///     cross-references (QSTI / ANAM / SCRO / CTDA FormIDs) through the legacy remap, which never
///     received them, so an INFO whose QSTI points at a planner-allocated new quest has its
///     reference nulled and is dropped (<c>droppedNoQstiInfos</c>). That is the Ulysses /
///     Gomorrah-greeter "force-greets but has no dialogue" regression the ESM eager-load surfaces.
///     Merging the planner allocations here restores resolution.
/// </remarks>
internal static class DialogPlannerRemapAugmentation
{
    /// <summary>
    ///     Merge every planner <c>source → emitted</c> mapping that the planner ACTUALLY emits
    ///     into <paramref name="sourceToAllocated" /> / <paramref name="sourceToAllocatedType" />.
    ///     Excludes: identity/zero sources; sources the legacy path already tracked (legacy wins);
    ///     allocated-but-unemitted orphans (<paramref name="emittedRecordType" /> returns null);
    ///     and DIAL/INFO, whose real FormIDs <c>DialogGrupBuilder</c> allocates itself.
    /// </summary>
    /// <param name="sourceToEmitted"><c>EmitPlan.SourceToEmittedFormId</c>.</param>
    /// <param name="emittedRecordType">
    ///     Record type (4-char signature) for an emitted plugin FormID, or <c>null</c> when the
    ///     FormID is not an emitted record (an allocated-but-unemitted orphan).
    /// </param>
    /// <param name="sourceToAllocated">Legacy source→allocated remap to augment in place.</param>
    /// <param name="sourceToAllocatedType">Companion source→record-type map to keep consistent.</param>
    public static void Merge(
        IReadOnlyDictionary<uint, uint> sourceToEmitted,
        Func<uint, string?> emittedRecordType,
        Dictionary<uint, uint> sourceToAllocated,
        Dictionary<uint, string> sourceToAllocatedType)
    {
        foreach (var (source, emitted) in sourceToEmitted)
        {
            if (source == 0 || source == emitted)
            {
                continue;
            }

            if (sourceToAllocated.ContainsKey(source))
            {
                continue;
            }

            var type = emittedRecordType(emitted);
            if (type is null or "DIAL" or "INFO")
            {
                continue;
            }

            sourceToAllocated[source] = emitted;
            sourceToAllocatedType[source] = type;
        }
    }
}
