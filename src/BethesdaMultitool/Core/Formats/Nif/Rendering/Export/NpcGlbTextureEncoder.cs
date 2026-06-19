using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Esm.Analysis;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Encodes a decoded texture to PNG bytes for embedding in an exported GLB.</summary>
internal static class NpcGlbTextureEncoder
{
    internal static byte[] EncodePng(DecodedTexture texture)
    {
        return PngWriter.EncodeRgba(texture.Pixels, texture.Width, texture.Height);
    }
}
