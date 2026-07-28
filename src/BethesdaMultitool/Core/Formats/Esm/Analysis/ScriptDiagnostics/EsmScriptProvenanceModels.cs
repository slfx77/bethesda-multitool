using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using ScriptReferenceSlot = BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics.EsmScriptBlockReader.ScriptReferenceSlot;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

/// <summary>Provenance report tracing how a generated ESP's scripts/dialogue relate to their source and master records.</summary>
public sealed record EsmScriptProvenanceReport(
    IReadOnlyList<EsmScriptSourceVsEmittedRefRow> SourceVsEmittedRefs,
    IReadOnlyList<EsmResultScriptProvenanceRow> ResultScripts,
    IReadOnlyList<EsmBytecodeEndianProbeRow> BytecodeEndianProbes,
    IReadOnlyList<EsmTargetStateTraceRow> StateTrace);

/// <summary>Compares a script reference slot in the emitted ESP against its matched source-record counterpart.</summary>
public sealed record EsmScriptSourceVsEmittedRefRow(
    string Target,
    string RecordType,
    uint EmittedFormId,
    string EmittedEditorId,
    int BlockIndex,
    int SlotIndex,
    string MatchStrategy,
    string SourceOrigin,
    uint SourceFormId,
    string SourceKind,
    uint SourceRawValue,
    string SourceLabel,
    string EmittedKind,
    uint EmittedRawValue,
    string EmittedLabel,
    string Classification);

/// <summary>Compares an emitted INFO result-script against its matched source INFO (block counts, SCDA hashes, SCTX).</summary>
public sealed record EsmResultScriptProvenanceRow(
    string Target,
    uint EmittedInfoFormId,
    uint SourceInfoFormId,
    string MatchStrategy,
    int SourceBlockCount,
    int EmittedBlockCount,
    string SourceScdaHashes,
    string EmittedScdaHashes,
    string SourceSctxPreview,
    string EmittedSctxPreview,
    string SourceReferenceCounts,
    string EmittedReferenceCounts,
    string Classification);

/// <summary>Walks one SCDA block as both little- and big-endian to diagnose byte-order of the compiled bytecode.</summary>
public sealed record EsmBytecodeEndianProbeRow(
    string Target,
    string Origin,
    string RecordType,
    uint FormId,
    string EditorId,
    int BlockIndex,
    int ByteLength,
    string FirstBytes,
    string LittleEndianOpcode,
    string BigEndianOpcode,
    bool LittleEndianWalkedToEnd,
    bool LittleEndianHasDiagnostics,
    string LittleEndianDiagnostics,
    bool BigEndianWalkedToEnd,
    bool BigEndianHasDiagnostics,
    string BigEndianDiagnostics,
    string Classification);

/// <summary>One traced link from a target record to a related record, categorized for state-flow analysis.</summary>
public sealed record EsmTargetStateTraceRow(
    string Target,
    string Category,
    string Relation,
    string RecordType,
    uint FormId,
    string EditorId,
    uint LinkedFormId,
    string LinkedLabel,
    string Detail);

/// <summary>One compiled-script block captured from an emitted/source/master record, used for provenance comparison.</summary>
internal sealed record BlockSnapshot(
    string Target,
    string Relation,
    string RecordType,
    uint FormId,
    string EditorId,
    int BlockIndex,
    byte[] Scda,
    string SourceText,
    IReadOnlyList<ScriptReferenceSlot> References,
    IReadOnlyList<ScriptVariableInfo> Variables,
    bool IsBigEndianBytecode,
    string Origin,
    string MatchStrategy);

/// <summary>Resolved record-type/editor/full-name labels for a FormID, used to annotate provenance rows.</summary>
internal sealed record LabelInfo(string RecordType, string EditorId, string FullName);
