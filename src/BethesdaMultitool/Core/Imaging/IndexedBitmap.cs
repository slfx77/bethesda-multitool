using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Imaging;

/// <summary>
///     An 8-bit indexed-colour image: row-major palette indices plus the draw offsets classic
///     sprite formats carry (FRM frame shifts, IMG/CIF screen positions). Pairs with a
///     <see cref="Palette" /> to produce the RGBA <c>DecodedTexture</c> the existing texture
///     seam (PNG writer, GPU cache, GUI) consumes.
/// </summary>
internal sealed class IndexedBitmap
{
    public IndexedBitmap(int width, int height, byte[] indices, int xOffset = 0, int yOffset = 0)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Length != width * height)
        {
            throw new ArgumentException(
                $"Indices length {indices.Length} does not match {width}x{height}.",
                nameof(indices));
        }

        Width = width;
        Height = height;
        Indices = indices;
        XOffset = xOffset;
        YOffset = yOffset;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Horizontal draw offset from the source format (0 when the format has none).</summary>
    public int XOffset { get; }

    /// <summary>Vertical draw offset from the source format (0 when the format has none).</summary>
    public int YOffset { get; }

    /// <summary>Row-major palette indices, length = Width * Height.</summary>
    public byte[] Indices { get; }

    /// <summary>
    ///     Resolves every index through <paramref name="palette" /> into a single-mip RGBA
    ///     <see cref="DecodedTexture" /> (r, g, b, a byte order, matching the DDS decoders).
    /// </summary>
    public DecodedTexture ToDecodedTexture(Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        var pixels = new byte[Width * Height * 4];
        var rgba = palette.Rgba;
        for (var i = 0; i < Indices.Length; i++)
        {
            var src = Indices[i] * 4;
            var dst = i * 4;
            pixels[dst] = rgba[src];
            pixels[dst + 1] = rgba[src + 1];
            pixels[dst + 2] = rgba[src + 2];
            pixels[dst + 3] = rgba[src + 3];
        }

        return DecodedTexture.FromBaseLevel(pixels, Width, Height, generateMipChain: false);
    }
}
