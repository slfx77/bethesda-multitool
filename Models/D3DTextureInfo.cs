namespace Xbox360MemoryCarver.Models;

/// <summary>
/// Xbox 360 D3D texture information extracted from DDX header.
/// </summary>
public class D3DTextureInfo
{
    /// <summary>
    /// Texture width in pixels.
    /// </summary>
    public uint Width { get; set; }

    /// <summary>
    /// Texture height in pixels.
    /// </summary>
    public uint Height { get; set; }

    /// <summary>
    /// DDS FourCC format code (e.g., DXT1, DXT5, ATI1, ATI2).
    /// </summary>
    public uint Format { get; set; }

    /// <summary>
    /// Xbox 360 GPU data format from header DWORD[3].
    /// </summary>
    public uint DataFormat { get; set; }

    /// <summary>
    /// Actual texture format (may differ from DataFormat for format 0x82).
    /// </summary>
    public uint ActualFormat { get; set; }

    /// <summary>
    /// Number of mipmap levels.
    /// </summary>
    public uint MipLevels { get; set; }

    /// <summary>
    /// Row pitch in bytes.
    /// </summary>
    public uint Pitch { get; set; }

    /// <summary>
    /// Whether the texture is tiled/swizzled.
    /// </summary>
    public bool Tiled { get; set; }

    /// <summary>
    /// Endianness indicator (0-3).
    /// </summary>
    public uint Endian { get; set; }

    /// <summary>
    /// Size of main texture data in bytes (all mip levels combined).
    /// </summary>
    public uint MainDataSize { get; set; }
}
