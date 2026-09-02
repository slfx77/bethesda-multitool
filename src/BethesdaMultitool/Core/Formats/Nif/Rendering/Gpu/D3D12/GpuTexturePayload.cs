using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>The pixel format a GPU texture payload is uploaded in: uncompressed RGBA8 or a block-compressed BC variant.</summary>
internal enum GpuTexturePayloadFormat
{
    Rgba8,
    BC1,
    BC2,
    BC3,
    BC4,
    BC5,
    BC7,
    // Keep signed variants at the end: the enum value is serialized by the persistent texture
    // cache, and retaining every existing value avoids reinterpreting an older cache entry.
    BC4S,
    BC5S
}

/// <summary>
///     How the shader decodes a normal-map sample. BC5 stores only X/Y; an SNORM SRV additionally
///     returns those channels already in [-1, 1], while UNORM/RGBA samples still need remapping.
/// </summary>
internal enum GpuNormalDecodeMode
{
    None = 0,
    Bc5ReconstructZ = 1,
    Bc5SignedReconstructZ = 2
}

/// <summary>One mip level of a GPU texture payload: its dimensions and raw (possibly block-compressed) bytes.</summary>
internal sealed record GpuTextureMipPayload(
    int Width,
    int Height,
    byte[] Bytes);

/// <summary>
///     A texture ready for GPU upload: its format, dimensions, and full mip chain. For cubemaps
///     (<see cref="ArraySize" /> = 6) <see cref="MipLevels" /> holds the six faces' chains
///     face-major (face 0 mip 0..N, face 1 mip 0..N, …) — the same order as both the DDS file
///     layout and D3D12's subresource indexing (mip + arraySlice × mipLevels).
/// </summary>
internal sealed record GpuTexturePayload(
    GpuTexturePayloadFormat Format,
    int Width,
    int Height,
    IReadOnlyList<GpuTextureMipPayload> MipLevels,
    int ArraySize = 1)
{
    public int MipCount => MipLevels.Count / Math.Max(ArraySize, 1);

    public bool IsCubemap => ArraySize == 6;

    public bool IsCompressed => Format != GpuTexturePayloadFormat.Rgba8;

    public GpuNormalDecodeMode NormalDecodeMode =>
        Format switch
        {
            GpuTexturePayloadFormat.BC5 => GpuNormalDecodeMode.Bc5ReconstructZ,
            GpuTexturePayloadFormat.BC5S => GpuNormalDecodeMode.Bc5SignedReconstructZ,
            _ => GpuNormalDecodeMode.None
        };

    public long ByteSize => MipLevels.Sum(static level => (long)level.Bytes.Length);

    public int BytesPerBlock => Format switch
    {
        GpuTexturePayloadFormat.BC1 or GpuTexturePayloadFormat.BC4 or GpuTexturePayloadFormat.BC4S => 8,
        GpuTexturePayloadFormat.BC2 or GpuTexturePayloadFormat.BC3 or GpuTexturePayloadFormat.BC5
            or GpuTexturePayloadFormat.BC5S or GpuTexturePayloadFormat.BC7 => 16,
        _ => 0
    };

    /// <summary>Builds an uncompressed RGBA8 payload (with its mip chain) from an already-decoded texture.</summary>
    public static GpuTexturePayload FromRgba(DecodedTexture decoded)
    {
        var levels = new List<GpuTextureMipPayload>(decoded.MipCount);
        for (var i = 0; i < decoded.MipCount; i++)
        {
            var level = decoded.GetMipLevel(i);
            levels.Add(new GpuTextureMipPayload(
                level.Width,
                level.Height,
                level.Pixels));
        }

        return new GpuTexturePayload(
            GpuTexturePayloadFormat.Rgba8,
            decoded.Width,
            decoded.Height,
            levels);
    }
}
