using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Per-source projection produced after <c>LoadSourceAsync</c> returns. Captures every
///     piece of state the cross-dump aggregator, cross-source builders, and HTML writers
///     read so the originating <c>SemanticSource</c> (with its full <c>RecordCollection</c>)
///     can be released. Peak load-phase memory drops from O(parsed records across all dumps)
///     to O(formatted reports + lightweight skeletons across all dumps).
/// </summary>
/// <remarks>
///     <para>
///         <b>Ordering invariant.</b> Per-source observations
///         (<see cref="WorldspaceObservations" />, <see cref="CellGroupObservations" />,
///         <see cref="DialogueObservations" />, <see cref="DialogTopicObservations" />) are
///         captured in load order. The aggregator reorders projections by build date and
///         replays the observations chronologically — this is what preserves worldspace
///         rename history (e.g. "Camp McCarran Tarmac → Camp McCarran") and the
///         ESM-authority gate on cell group assignment.
///     </para>
///     <para>
///         <b>Pass-A reports.</b> <see cref="ReportsByType" /> holds the
///         <see cref="RecordReport" /> for every record type that doesn't need cross-source
///         enrichment (every type except NPC, Key, Container). Pass-B in
///         <see cref="CrossDumpComparisonPipeline" /> populates the missing three types after
///         every projection is built (because their reports depend on
///         <see cref="NpcPlacementInfo" />, <see cref="KeyLockedDoorInfo" />, and
///         <see cref="ContainerPlacementInfo" /> indexes that need every source's cells).
///     </para>
///     <para>
///         <b>Pass-B holdover records.</b> The full <see cref="NpcRecord" />, <see cref="KeyRecord" />,
///         and <see cref="ContainerRecord" /> instances are stashed on
///         <see cref="LateEnrichmentRecords" /> and only released after pass-B finishes —
///         each of these record lists is small (~MBs not GBs) compared to the cell/dialogue
///         lists that get released immediately.
///     </para>
/// </remarks>
internal sealed record CrossDumpSourceProjection
{
    public required string FilePath { get; init; }
    public required string ShortName { get; init; }
    public required bool IsDmp { get; init; }
    public required DateTime BuildDateUtc { get; init; }
    public required string DateSource { get; init; }

    public required FormIdResolver Resolver { get; init; }
    public MinidumpInfo? MinidumpInfo { get; init; }

    /// <summary>
    ///     Pass-A reports, keyed by record type name then FormID. NPC, Key, and Container
    ///     entries are filled in by pass-B once cross-source indexes exist.
    /// </summary>
    public required Dictionary<string, Dictionary<uint, RecordReport>> ReportsByType { get; init; }

    /// <summary>Worldspace identity observations for chronological replay.</summary>
    public required IReadOnlyList<WorldspaceObservation> WorldspaceObservations { get; init; }

    /// <summary>Cell-group inputs for chronological replay (interior detection + grid coords + ESM authority).</summary>
    public required IReadOnlyList<CellGroupObservation> CellGroupObservations { get; init; }

    /// <summary>Per-dialogue metadata captured at projection time (quest/speaker/topic FormIDs + first prompt/response text).</summary>
    public required IReadOnlyDictionary<uint, DialogueObservation> DialogueObservations { get; init; }

    /// <summary>Per-DialogTopic metadata + search text (search text is pre-built from <c>Dialogues</c> before the dialogue list is released).</summary>
    public required IReadOnlyDictionary<uint, DialogTopicObservation> DialogTopicObservations { get; init; }

    /// <summary>Worldspace identity lookup for direct <c>BuildWorldspaceNameLookup</c> use (Resolver fallback if missing).</summary>
    public required IReadOnlyDictionary<uint, WorldspaceNameEntry> WorldspaceNames { get; init; }

    /// <summary>Cell skeletons feeding the cross-source virtual-cell canonicalizer and placement indexes.</summary>
    public required IReadOnlyList<CellSkeleton> CellSkeletons { get; init; }

    /// <summary>NPC FormID set for placement / script reference indexes.</summary>
    public required IReadOnlyList<NpcSkeleton> NpcSkeletons { get; init; }

    /// <summary>Key FormID set for the locked-door reverse index.</summary>
    public required IReadOnlyList<KeySkeleton> KeySkeletons { get; init; }

    /// <summary>Container FormID set for the container placement index.</summary>
    public required IReadOnlyList<ContainerSkeleton> ContainerSkeletons { get; init; }

    /// <summary>Script skeletons for the NPC→Script reverse index.</summary>
    public required IReadOnlyList<ScriptSkeleton> ScriptSkeletons { get; init; }

    /// <summary>
    ///     Records kept alive specifically for pass-B <c>BuildReport</c> calls (NPC / Key / Container reports
    ///     depend on cross-source indexes). Nulled-out after pass-B completes to release the held memory.
    /// </summary>
    public LateEnrichmentRecords? LateEnrichment { get; init; }
}

/// <summary>Worldspace name entry mirrored from <c>WorldspaceRecord</c> at projection time.</summary>
internal readonly record struct WorldspaceNameEntry(string? EditorId, string? FullName);

/// <summary>
///     The full record lists kept alive specifically for pass-B report building. Released after
///     <see cref="CrossDumpComparisonPipeline" /> runs the cross-source-dependent
///     <c>BuildReport</c> calls.
/// </summary>
internal sealed record LateEnrichmentRecords
{
    public required IReadOnlyList<NpcRecord> Npcs { get; init; }
    public required IReadOnlyList<KeyRecord> Keys { get; init; }
    public required IReadOnlyList<ContainerRecord> Containers { get; init; }
}
