using System.Text.Json;
using System.Text.Json.Serialization;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeQuestConditionStoredRow(int SubrecordIndex, int? SourceRow, string Scope,
    long? PayloadOffset, string OffsetStatus, string RawHex, string Status, uint? Parameter1FormId,
    uint? ReferenceFormId);

/// <summary>Every competing physical record is retained; Selected denotes the load-order winner only.</summary>
public sealed record RuntimeQuestConditionRecord(string Identity, int PluginIndex, string Plugin,
    string PluginSha256, string SourcePath, string RecordType, uint FileLocalFormId, uint LoadOrderFormId,
    string? DefiningPlugin, uint Flags, bool NamespaceClamped, long RecordOffset, bool Selected, string Status,
    IReadOnlyList<RuntimeQuestConditionStoredRow> Rows);

public sealed record RuntimeQuestConditionRowMatch(int TraceLine, ulong Sequence, int SourceRow,
    int? SubrecordIndex, long? PayloadOffset, string Status, string Reason, JsonElement RawObservation);

public sealed record RuntimeQuestConditionMapping(ulong? RequestId, ulong? InvocationId, uint? OwnerFormId,
    string SourceStatus, string Reason, string? RecordIdentity, IReadOnlyList<string> CandidateRecordIdentities,
    IReadOnlyList<RuntimeQuestConditionRowMatch> Rows, JsonElement? NativeTerminal);

public sealed record RuntimeQuestConditionSourceMapReport(string Schema, int Version, string TraceSha256,
    RuntimeTraceBinding Binding, IReadOnlyList<RuntimeSourcePlugin> Sources,
    IReadOnlyList<RuntimeQuestConditionRecord> Records, IReadOnlyList<RuntimeQuestConditionMapping> Mappings,
    IReadOnlyList<string> Limitations);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RuntimeQuestConditionSourceMapReport))]
internal sealed partial class RuntimeQuestConditionSourceJsonContext : JsonSerializerContext { }
