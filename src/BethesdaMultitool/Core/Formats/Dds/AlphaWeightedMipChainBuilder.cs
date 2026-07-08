namespace BethesdaMultitool.Core.Formats.Dds;

/// <summary>
///     Rebuilds a mip chain from a decoded RGBA base level using ALPHA-WEIGHTED color averaging.
///     Shipped foliage atlases (SpeedTree leaf cutouts) store their RGB independently of coverage, so
///     the compiled DDS mips average leaf color with the atlas BACKGROUND color — white for e.g. the
///     Oblivion dogwood/maple composites. At distance the sampled mips then wash the whole canopy
///     toward the background ("giant white flowers"). The engine never renders leaf geometry at those
///     mip levels (distant trees switch to billboard imposters), so the shipped mips were never
///     authored for it. Weighting each 2×2 box average by the texels' alpha keeps the mip RGB the
///     color of the actual foliage; alpha itself stays a plain average (the coverage semantics the
///     alpha test + Castaño boost expect).
/// </summary>
internal static class AlphaWeightedMipChainBuilder
{
    public static IReadOnlyList<DecodedTextureMipLevel> Build(byte[] basePixels, int width, int height)
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

                    var i00 = (srcY0 * currentWidth + srcX0) * 4;
                    var i10 = (srcY0 * currentWidth + srcX1) * 4;
                    var i01 = (srcY1 * currentWidth + srcX0) * 4;
                    var i11 = (srcY1 * currentWidth + srcX1) * 4;

                    var a00 = currentPixels[i00 + 3];
                    var a10 = currentPixels[i10 + 3];
                    var a01 = currentPixels[i01 + 3];
                    var a11 = currentPixels[i11 + 3];
                    var alphaSum = a00 + a10 + a01 + a11;

                    var dstIndex = (y * nextWidth + x) * 4;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        int value;
                        if (alphaSum > 0)
                        {
                            // Color = alpha-weighted average: background (alpha 0) texels contribute
                            // nothing, so foliage color survives minification instead of washing out.
                            value = (currentPixels[i00 + channel] * a00 +
                                     currentPixels[i10 + channel] * a10 +
                                     currentPixels[i01 + channel] * a01 +
                                     currentPixels[i11 + channel] * a11 +
                                     alphaSum / 2) / alphaSum;
                        }
                        else
                        {
                            // Fully transparent box: keep a plain average so bilinear neighbors that
                            // straddle the coverage edge don't pull toward black.
                            value = (currentPixels[i00 + channel] +
                                     currentPixels[i10 + channel] +
                                     currentPixels[i01 + channel] +
                                     currentPixels[i11 + channel] + 2) / 4;
                        }

                        nextPixels[dstIndex + channel] = (byte)Math.Clamp(value, 0, 255);
                    }

                    nextPixels[dstIndex + 3] = (byte)((alphaSum + 2) / 4);
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
