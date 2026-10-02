using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

/// <summary>
/// One physical QUST script bundle. Ordinals are one-based, except subrecord indexes which
/// are zero-based. Null stage/entry values identify unowned fragments. Byte arrays retain
/// the analyzed payloads; StoredSource includes the original trailing NUL when present.
/// </summary>
public sealed record EsmQuestScriptFragment(
    uint FormId,
    string? EditorId,
    long RecordOffset,
    int RecordOccurrence,
    uint RecordFlags,
    int BlockIndex,
    int? StageOrdinal,
    ushort? StageIndex,
    int? EntryOrdinal,
    int StartSubrecordIndex,
    int? ScdaSubrecordIndex,
    long? ScdaFileOffset,
    string OffsetStatus,
    string HeaderState,
    uint? DeclaredCompiledSize,
    uint? DeclaredReferenceCount,
    uint? DeclaredVariableCount,
    string BytecodeState,
    string SourceState,
    string Status,
    string? ByteOrder,
    string? ByteOrderEvidence,
    string? ByteOrderDetail,
    byte[]? Bytecode,
    byte[]? StoredSource,
    string? Reconstruction,
    IReadOnlyList<ScriptVariableInfo> Variables,
    IReadOnlyList<EsmQuestScriptReference> References,
    IReadOnlyList<ScriptExternalVariableBinding> ExternalVariables,
    IReadOnlyList<EsmQuestScriptMetadata> Metadata,
    IReadOnlyList<string> Diagnostics);

/// <summary>One ordered SCRO/SCRV slot, including malformed slots whose value is unavailable.</summary>
public sealed record EsmQuestScriptReference(int SlotIndex, string Kind, uint? RawValue, int SubrecordIndex)
{
    /// <summary>Preserves the slot while adapting its value to the shared decoder's local-reference marker.</summary>
    internal uint DecoderValue
    {
        get
        {
            if (RawValue is not { } value) return 0;
            return Kind == "SCRV" ? value | 0x80000000 : value;
        }
    }
}

/// <summary>Original header, table and stored-source bytes within this bundle, including duplicates.</summary>
public sealed record EsmQuestScriptMetadata(string Signature, int SubrecordIndex, byte[] Data);
