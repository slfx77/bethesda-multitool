using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.Core.Formats.Esm.Xref;

internal sealed record FormIdEdge(string SourcePlugin, string SourcePath, uint SourceFileLocalFormId,
    uint SourceLoadOrderFormId, string SourceSignature, string? SourceEditorId,
    uint TargetFileLocalFormId, uint TargetLoadOrderFormId, string? TargetOwner,
    string TargetStatus, string Kind, string Certainty, string Subrecord, int SubrecordOrdinal,
    int SubrecordOccurrence, string Field, int FieldOffset, long RecordOffset, int PayloadOffset,
    long? FileOffset, bool Compressed, bool WasClamped, bool? OppositeEnableState = null,
    bool SourceTypeConflict = false, bool SourceAmbiguous = false);

internal sealed class ReferenceCoverage
{
    public int RecordsScanned { get; set; }
    public int RecordsDecoded { get; set; }
    public int CompressedRecords { get; set; }
    public int UnreadableRecords { get; set; }
    public int FilteredVersions { get; set; }
    public int FilteredTypeConflicts { get; set; }
    public int FilteredAmbiguousSources { get; set; }
    public HashSet<string> UnmodeledRecordTypes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> RawSubrecords { get; } = new(StringComparer.Ordinal);
    public const string Caveat = "Zero inbound typed references is not proof of non-use. This is a bounded static index, not engine reachability.";
    public static readonly string[] NotIndexed =
    [
        "Dynamic script lookups, numeric IDs constructed at runtime, assets and external files",
        "SCDA instruction semantics (SCRO reference-table entries are indexed, not proof of execution)",
        "Schema-opaque fields and unresolved union deciders (reported separately when observable)",
        "Memory dumps and their runtime pointer graphs; this command accepts complete plugins only",
        "Runtime quest/package/dialogue conditions, enable state and full gameplay reachability",
        "Engine merge semantics for repeated physical records in a winning plugin; default queries exclude these ambiguous sources. Use --all-versions for labeled physical evidence, excluded from traversal."
    ];
}

internal sealed record ReferenceReport(uint Target, PluginLoadOrder Order, LoadOrderRecordIndex Index,
    bool AllVersions, IReadOnlyList<FormIdEdge> Inbound, IReadOnlyList<FormIdEdge> Outbound,
    IReadOnlyList<FormIdEdge> Uncertain, IReadOnlyList<FormIdEdge> Untyped, ReferenceCoverage Coverage,
    StaticReferenceTraversal? Traversal = null);
