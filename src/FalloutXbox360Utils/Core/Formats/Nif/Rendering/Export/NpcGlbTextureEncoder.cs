using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal static class NpcGlbTextureEncoder
{
    internal static byte[] EncodePng(DecodedTexture texture)
    {
        return PngWriter.EncodeRgba(texture.Pixels, texture.Width, texture.Height);
    }
}
