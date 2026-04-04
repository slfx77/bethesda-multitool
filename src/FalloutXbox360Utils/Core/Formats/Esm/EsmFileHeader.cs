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

    /// <summary>
    ///     Detected game based on the HEDR version float.
    ///     FO3 = 0.94, FNV = 1.32–1.35.
    /// </summary>
    public FalloutGame DetectedGame => Version switch
    {
        >= 0.93f and <= 0.95f => FalloutGame.Fallout3,
        >= 1.0f => FalloutGame.FalloutNewVegas,
        _ => FalloutGame.Unknown
    };
}
