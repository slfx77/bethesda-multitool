namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     A texture extracted from runtime NiPixelData in a memory dump.
///     Contains raw pixel data (typically DXT-compressed blocks) ready for DDS export.
/// </summary>
public record ExtractedTexture
{
    /// <summary>File offset of the NiPixelData struct in the dump.</summary>
    public long SourceOffset { get; init; }

    /// <summary>Texture width in pixels (mip level 0).</summary>
    public int Width { get; init; }

    /// <summary>Texture height in pixels (mip level 0).</summary>
    public int Height { get; init; }

    /// <summary>Number of mipmap levels.</summary>
    public int MipmapLevels { get; init; }

    /// <summary>Pixel format (NiPixelFormat::Format enum value).</summary>
    public NiTextureFormat Format { get; init; }

    /// <summary>Bits per pixel from NiPixelFormat.m_ucBitsPerPixel.</summary>
    public int BitsPerPixel { get; init; }

    /// <summary>Number of cubemap faces (1 for 2D textures, 6 for cubemaps).</summary>
    public int Faces { get; init; }

    /// <summary>Raw pixel data (DXT blocks or uncompressed pixels).</summary>
    public required byte[] PixelData { get; init; }

    /// <summary>Total pixel data size in bytes.</summary>
    public int DataSize => PixelData.Length;

    /// <summary>Texture filename from NiSourceTexture, if found.</summary>
    public string? Filename { get; init; }

    /// <summary>Whether this is a block-compressed format (DXT1/3/5).</summary>
    public bool IsCompressed => Format is NiTextureFormat.DXT1 or NiTextureFormat.DXT3 or NiTextureFormat.DXT5;

    /// <summary>Whether this is a cubemap (6 faces).</summary>
    public bool IsCubemap => Faces > 1;

    /// <summary>Hash for deduplication (first 64 bytes of pixel data).</summary>
    public long DataHash { get; init; }

    /// <summary>
    ///     Effective bits per pixel for uncompressed textures.
    ///     Runtime m_ucBitsPerPixel is often unreliable (0 for compressed, sometimes wrong for uncompressed).
    ///     Infers from data size and dimensions when the stored value seems wrong.
    /// </summary>
    public int EffectiveBitsPerPixel
    {
        get
        {
            if (IsCompressed)
            {
                return 0;
            }

            // Calculate total uncompressed pixels across all mip levels and faces
            var totalPixels = 0L;
            for (var mip = 0; mip < MipmapLevels; mip++)
            {
                var mipW = Math.Max(1, Width >> mip);
                var mipH = Math.Max(1, Height >> mip);
                totalPixels += mipW * mipH;
            }

            totalPixels *= Faces;

            if (totalPixels <= 0)
            {
                return BitsPerPixel;
            }

            // Infer from data size
            var inferred = (int)(DataSize * 8L / totalPixels);

            // Validate: must be a standard BPP value
            return inferred is 8 or 16 or 24 or 32 ? inferred : BitsPerPixel;
        }
    }
}

/// <summary>
///     NiPixelFormat::Format enum values from Gamebryo engine (PDB-verified via nif.xml).
/// </summary>
public enum NiTextureFormat
{
    RGB = 0,
    RGBA = 1,
    PAL = 2,
    PALA = 3,
    DXT1 = 4,
    DXT3 = 5,
    DXT5 = 6,
    RGB24NonInt = 7,
    Bump = 8,
    BumpLuma = 9,
    RenderSpec = 10,
    OneCh = 11,
    TwoCh = 12,
    ThreeCh = 13
}
