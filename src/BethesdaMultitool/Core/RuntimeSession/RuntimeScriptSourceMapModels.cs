using System.Text.Json;
using System.Text.Json.Serialization;
using BethesdaMultitool.Core.Formats.Esm.Script;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeScriptReference(int Slot, string Kind, uint RawValue, uint? LoadOrderFormId);
public sealed record RuntimeScriptLocal(uint Index, string? Name, byte Type);

/// <summary>One canonical physical block; names never supply its identity.</summary>
public sealed record RuntimeScriptBlock(string Identity, int PluginIndex, string Plugin, string PluginSha256,
    string SourcePath, string RecordType, uint FileLocalFormId, uint LoadOrderFormId, long RecordOffset,
    int BlockIndex, int ScdaSubrecordIndex, long? ScdaFileOffset, string ScdaOffsetStatus,
    string ScdaSha256, int ScdaLength, string ByteOrder, string ByteOrderEvidence, string MetadataSha256,
    IReadOnlyList<RuntimeScriptReference> References, IReadOnlyList<RuntimeScriptLocal> Locals,
    string? StoredSourceSha256, ScriptInstructionMap Reconstruction);

public sealed record RuntimeScriptMapping(int TraceLine, int TraceByteOffset, int TraceByteLength,
    ulong Sequence, string EventKind, uint? ScriptFormId, string OwnerStatus, string OwnerReason,
    string BytecodeStatus, string BytecodeReason, string LocationStatus, string LocationReason,
    IReadOnlyList<string> CandidateBlockIdentities, string? BlockIdentity, int? ScdaOffset,
    long? SourceFileOffset, ScriptInstructionSpan? Instruction, JsonElement? RawLocation);

public sealed record RuntimeScriptSourceMapReport(string Schema, int Version, string TraceSha256,
    RuntimeTraceBinding Binding, IReadOnlyList<RuntimeSourcePlugin> Sources,
    IReadOnlyList<RuntimeScriptBlock> Blocks, IReadOnlyList<RuntimeScriptMapping> Mappings,
    IReadOnlyList<string> Limitations);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RuntimeScriptSourceMapReport))]
internal sealed partial class RuntimeScriptSourceMapJsonContext : JsonSerializerContext { }
