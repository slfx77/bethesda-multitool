namespace FalloutXbox360Utils.Core.Formats.Dds;

/// <summary>
///     A decoded RGBA texture ready for sampling.
/// </summary>
internal sealed class DecodedTexture
{
    public required IReadOnlyList<DecodedTextureMipLevel> MipLevels { get; init; }

    /// <summary>RGBA pixel data for mip 0.</summary>
    public byte[] Pixels => MipLevels[0].Pixels;

    public int Width => MipLevels[0].Width;
    public int Height => MipLevels[0].Height;
    public int MipCount => MipLevels.Count;

    /// <summary>
    ///     Returns true if the texture has a significant number of fully transparent pixels,
    ///     indicating it's an overlay/decal that needs alpha testing even without explicit NiAlphaProperty.
    /// </summary>
    public bool HasSignificantAlpha()
    {
        var pixels = Pixels;
        if (pixels.Length < 4) return false;

        var totalPixels = pixels.Length / 4;
        var transparentCount = 0;

        // Sample every 4th pixel for performance on large textures
        var step = Math.Max(1, totalPixels / 4096);
        var sampled = 0;
        for (var i = 3; i < pixels.Length; i += 4 * step)
        {
            sampled++;
            if (pixels[i] < 128)
                transparentCount++;
        }

        // If >10% of sampled pixels are mostly transparent, this texture needs alpha
        return sampled > 0 && transparentCount > sampled / 10;
    }

    public DecodedTextureMipLevel GetMipLevel(int level)
    {
        var clamped = Math.Clamp(level, 0, MipLevels.Count - 1);
        return MipLevels[clamped];
    }

    public static DecodedTexture FromBaseLevel(
        byte[] pixels,
        int width,
        int height,
        bool generateMipChain = true)
    {
        var levels = generateMipChain
            ? BuildMipChain(pixels, width, height)
            :
            [
                new DecodedTextureMipLevel
                {
                    Pixels = pixels,
                    Width = width,
                    Height = height
                }
            ];

        return new DecodedTexture { MipLevels = levels };
    }

    internal static IReadOnlyList<DecodedTextureMipLevel> BuildMipChain(
        byte[] basePixels,
        int width,
        int height)
    {
        var levels = new List<DecodedTextureMipLevel>
        {
            new()
            {
                Pixels = basePixels,
                Width = width,
                Height = height
            }
        };

        var currentPixels = basePixels;
        var currentWidth = width;
        var currentHeight = height;

        while (currentWidth > 1 || currentHeight > 1)
        {
            var nextWidth = Math.Max(1, currentWidth >> 1);
            var nextHeight = Math.Max(1, currentHeight >> 1);
            var nextPixels = new byte[nextWidth * nextHeight * 4];

            for (var y = 0; y < nextHeight; y++)
            {
                for (var x = 0; x < nextWidth; x++)
                {
                    var srcX0 = Math.Min(currentWidth - 1, x * 2);
                    var srcY0 = Math.Min(currentHeight - 1, y * 2);
                    var srcX1 = Math.Min(currentWidth - 1, srcX0 + 1);
                    var srcY1 = Math.Min(currentHeight - 1, srcY0 + 1);
                    var dstIndex = (y * nextWidth + x) * 4;

                    for (var channel = 0; channel < 4; channel++)
                    {
                        var sample00 =
                            currentPixels[(srcY0 * currentWidth + srcX0) * 4 + channel];
                        var sample10 =
                            currentPixels[(srcY0 * currentWidth + srcX1) * 4 + channel];
                        var sample01 =
                            currentPixels[(srcY1 * currentWidth + srcX0) * 4 + channel];
                        var sample11 =
                            currentPixels[(srcY1 * currentWidth + srcX1) * 4 + channel];

                        nextPixels[dstIndex + channel] = (byte)(
                            (sample00 + sample10 + sample01 + sample11 + 2) / 4);
                    }
                }
            }

            levels.Add(new DecodedTextureMipLevel
            {
                Pixels = nextPixels,
                Width = nextWidth,
                Height = nextHeight
            });

            currentPixels = nextPixels;
            currentWidth = nextWidth;
            currentHeight = nextHeight;
        }

        return levels;
    }
}
