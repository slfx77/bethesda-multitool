using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed record DialogueInfoProvenanceReport
{
    public DialogueRecord Dialogue { get; init; } = null!;
    public IReadOnlyList<DialogueTesFileMappingSegment> TesFileSegments { get; init; } = [];
    public DialogueTesFileScriptRecoveryResult ResultScriptRecovery { get; init; } = null!;
    public byte[]? RuntimeStructBytes { get; init; }
    public uint? ConversationDataPointer { get; init; }
    public long? ConversationDataOffset { get; init; }
    public byte[]? ConversationDataBytes { get; init; }
}