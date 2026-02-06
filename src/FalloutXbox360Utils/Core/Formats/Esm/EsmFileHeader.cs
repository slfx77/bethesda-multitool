namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     ESM file header (TES4 record contents).
/// </summary>
public record EsmFileHeader
{
    public float Version { get; init; }
    public uint NextObjectId { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Masters { get; init; } = [];
    public uint RecordFlags { get; init; }
    public bool IsBigEndian { get; init; }
}
