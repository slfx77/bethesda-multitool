namespace EsmAnalyzer.Helpers;

/// <summary>
///     Difference between subrecords.
/// </summary>
public sealed record SubrecordDiff
{
    public required string Signature { get; init; }
    public required int Xbox360Size { get; init; }
    public required int PcSize { get; init; }
    public int Xbox360Offset { get; init; }
    public int PcOffset { get; init; }
    public byte[]? Xbox360Data { get; init; }
    public byte[]? PcData { get; init; }
    public string? DiffType { get; init; }
}