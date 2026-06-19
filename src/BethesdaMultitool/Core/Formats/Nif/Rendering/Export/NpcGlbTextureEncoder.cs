using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Esm.Analysis;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

internal static class NpcGlbTextureEncoder
{
    internal static byte[] EncodePng(DecodedTexture texture)
    {
        return PngWriter.EncodeRgba(texture.Pixels, texture.Width, texture.Height);
    }
}
