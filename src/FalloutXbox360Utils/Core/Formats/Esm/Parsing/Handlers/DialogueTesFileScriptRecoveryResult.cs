using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed record DialogueTesFileScriptRecoveryResult
{
    public DialogueTesFileScriptRecoveryStatus Status { get; init; }
    public uint TesFileOffset { get; init; }
    public long? SegmentBaseVirtualAddress { get; init; }
    public long? TargetVirtualAddress { get; init; }
    public long? MappedDumpOffset { get; init; }
    public string? Signature { get; init; }
    public uint? RecordFormId { get; init; }
    public uint? RecordFlags { get; init; }
    public uint? RecordDataSize { get; init; }
    public byte[]? HeaderBytes { get; init; }
    public byte[]? RecordDataBytes { get; init; }
    public List<DialogueResultScript> Scripts { get; init; } = [];
}