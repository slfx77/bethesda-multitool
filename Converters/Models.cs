namespace Xbox360MemoryCarver.Converters;

/// <summary>
/// Xbox 360 D3D texture information structure.
/// Contains format and dimension information extracted from DDX headers.
/// </summary>
public class D3DTextureInfo
{
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint Format { get; set; }
    public uint DataFormat { get; set; }
    public uint ActualFormat { get; set; }
    public uint MipLevels { get; set; }
    public uint Pitch { get; set; }
    public bool Tiled { get; set; }
    public uint Endian { get; set; }
    public uint MainDataSize { get; set; }
}

/// <summary>
/// Options for DDX to DDS conversion.
/// </summary>
public class ConversionOptions
{
    /// <summary>Save the mip atlas as a separate DDS file.</summary>
    public bool SaveAtlas { get; set; }

    /// <summary>Save raw decompressed data for debugging.</summary>
    public bool SaveRaw { get; set; }

    /// <summary>Save individual mip levels as separate DDS files.</summary>
    public bool SaveMips { get; set; }

    /// <summary>Skip untiling/unswizzling of the atlas chunk.</summary>
    public bool NoUntileAtlas { get; set; }

    /// <summary>Skip byte-swapping for endianness conversion.</summary>
    public bool SkipEndianSwap { get; set; }

    /// <summary>Enable verbose logging output.</summary>
    public bool Verbose { get; set; }
}
