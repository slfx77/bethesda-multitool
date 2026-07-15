namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;

/// <summary>
///     Bridges the two-pass planner's new-record FormID allocations into the legacy pipeline's
///     remap and emitted-record registries.
/// </summary>
/// <remarks>
///     Under <c>--planner-types all</c> new proto records are emitted by <c>PlanWriter</c>, whose
///     dispatch returns before the legacy <c>TrackNewRecordSourceAlias</c> path. Their
///     <c>source → emitted</c> mappings therefore live only in <c>EmitPlan</c>. Every later
///     legacy-routed encoder still needs those mappings: dialogue needs QSTI/ANAM remaps, and
///     ordinary records such as ACTI/DOOR/FURN/QUST need SCRI remaps to planner-allocated SCPTs.
///     Without the bridge the script itself is emitted under a plugin FormID while its owner
///     keeps the obsolete prototype FormID, producing runtime "Unable to find script" errors.
/// </remarks>
internal static class PlannerLegacyStateBridge
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

    /// <summary>
    ///     Registers planner-owned new records in the legacy type-aware emitted sets. Those
    ///     sets validate SCRI/base/package references in legacy encoders. DIAL/INFO are excluded
    ///     because <c>DialogGrupBuilder</c> owns their actual allocation and emission.
    /// </summary>
    public static void RegisterEmittedNewRecords(
        IEnumerable<BethesdaMultitool.Core.Formats.Esm.Planner.RecordPlan> records,
        IReadOnlySet<string> skippedRecordTypes,
        Action<string, uint> register)
    {
        foreach (var record in records)
        {
            if (record.Disposition != BethesdaMultitool.Core.Formats.Esm.Planner.RecordDisposition.New
                || record.Type is "DIAL" or "INFO"
                || skippedRecordTypes.Contains(record.Type))
            {
                continue;
            }

            register(record.Type, record.FormId);
        }
    }
}
