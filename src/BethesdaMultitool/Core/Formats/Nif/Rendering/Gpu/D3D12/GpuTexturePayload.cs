using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

internal enum GpuTexturePayloadFormat
{
    Rgba8,
    BC1,
    BC2,
    BC3,
    BC4,
    BC5
}

internal enum GpuNormalDecodeMode
{
    None = 0,
    Bc5ReconstructZ = 1
}

internal sealed record GpuTextureMipPayload(
    int Width,
    int Height,
    byte[] Bytes);

internal sealed record GpuTexturePayload(
    GpuTexturePayloadFormat Format,
    int Width,
    int Height,
    IReadOnlyList<GpuTextureMipPayload> MipLevels)
{
    public int MipCount => MipLevels.Count;

    public bool IsCompressed => Format != GpuTexturePayloadFormat.Rgba8;

    public GpuNormalDecodeMode NormalDecodeMode =>
        Format == GpuTexturePayloadFormat.BC5
            ? GpuNormalDecodeMode.Bc5ReconstructZ
            : GpuNormalDecodeMode.None;

    public long ByteSize => MipLevels.Sum(static level => (long)level.Bytes.Length);

    public int BytesPerBlock => Format switch
    {
        GpuTexturePayloadFormat.BC1 or GpuTexturePayloadFormat.BC4 => 8,
        GpuTexturePayloadFormat.BC2 or GpuTexturePayloadFormat.BC3 or GpuTexturePayloadFormat.BC5 => 16,
        _ => 0
    };

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
