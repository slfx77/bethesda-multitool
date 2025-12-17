namespace Xbox360MemoryCarver.Models;

/// <summary>
/// Options for DDX to DDS conversion.
/// </summary>
public class ConversionOptions
{
    /// <summary>
    /// Save untiled mip atlas as separate DDS file.
    /// </summary>
    public bool SaveAtlas { get; set; }

    /// <summary>
    /// Save raw combined decompressed data as binary file.
    /// </summary>
    public bool SaveRaw { get; set; }

    /// <summary>
    /// Save extracted mip levels from atlas.
    /// </summary>
    public bool SaveMips { get; set; }

    /// <summary>
    /// Do not untile/unswizzle the atlas (leave tiled).
    /// </summary>
    public bool NoUntileAtlas { get; set; }

    /// <summary>
    /// Do not perform endian swap on data.
    /// </summary>
    public bool SkipEndianSwap { get; set; }

    /// <summary>
    /// Enable verbose output for debugging.
    /// </summary>
    public bool Verbose { get; set; }

    /// <summary>
    /// Swap atlas/main chunk order when two-chunk payload detected.
    /// </summary>
    public bool SwapChunks { get; set; }
}
