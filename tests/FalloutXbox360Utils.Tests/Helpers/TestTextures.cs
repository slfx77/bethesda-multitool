using FalloutXbox360Utils.Core.Formats.Dds;
using Xunit;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     Builders for synthetic <see cref="DecodedTexture"/> instances used by NIF rendering
///     and texture-resolver tests. Centralizes the small "make a 1x1" / "make a uniform fill" /
///     "make from explicit texels" idioms so they don't get re-implemented in every file.
/// </summary>
internal static class TestTextures
{
    /// <summary>1x1 RGBA texture with the supplied pixel color.</summary>
    public static DecodedTexture Single(byte r, byte g, byte b, byte a)
    {
        return DecodedTexture.FromBaseLevel([r, g, b, a], 1, 1);
    }

    /// <summary>
    ///     <paramref name="width"/>x<paramref name="height"/> RGBA texture filled with the
    ///     supplied color (defaults to fully opaque).
    /// </summary>
    public static DecodedTexture Uniform(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = r;
            pixels[offset + 1] = g;
            pixels[offset + 2] = b;
            pixels[offset + 3] = a;
        }

        return DecodedTexture.FromBaseLevel(pixels, width, height);
    }

    /// <summary>
    ///     RGBA texture built from explicit per-pixel <paramref name="texels"/> in row-major
    ///     order. Asserts the texel count matches <paramref name="width"/> * <paramref name="height"/>.
    /// </summary>
    public static DecodedTexture FromTexels(int width, int height, params (byte R, byte G, byte B, byte A)[] texels)
    {
        Assert.Equal(width * height, texels.Length);

        var pixels = new byte[texels.Length * 4];
        for (var i = 0; i < texels.Length; i++)
        {
            var offset = i * 4;
            pixels[offset] = texels[i].R;
            pixels[offset + 1] = texels[i].G;
            pixels[offset + 2] = texels[i].B;
            pixels[offset + 3] = texels[i].A;
        }

        return DecodedTexture.FromBaseLevel(pixels, width, height);
    }
}
