using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed record DialogTopicProvenanceReport
{
    public DialogTopicRecord Topic { get; init; } = null!;
    public byte[]? RuntimeStructBytes { get; init; }
    public uint? StringPointer { get; init; }
    public ushort? StringLength { get; init; }
    public long? StringOffset { get; init; }
    public byte[]? StringBytes { get; init; }
    public string? DecodedText { get; init; }
}