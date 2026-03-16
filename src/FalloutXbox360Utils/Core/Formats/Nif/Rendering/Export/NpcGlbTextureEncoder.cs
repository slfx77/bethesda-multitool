using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal static class NpcGlbTextureEncoder
{
    internal static byte[] EncodePng(DecodedTexture texture, bool flipGreenChannel = false)
    {
        var pixels = flipGreenChannel
            ? FlipGreenChannel(texture.Pixels)
            : texture.Pixels;
        return PngWriter.EncodeRgba(pixels, texture.Width, texture.Height);
    }

    internal static byte[] FlipGreenChannel(byte[] pixels)
    {
        var copy = (byte[])pixels.Clone();
        for (var index = 1; index < copy.Length; index += 4)
        {
            copy[index] = (byte)(255 - copy[index]);
        }

        return copy;
    }
}
