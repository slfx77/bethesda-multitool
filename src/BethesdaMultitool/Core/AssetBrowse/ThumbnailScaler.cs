// Scaling rules ported from JimmyPCTool / AweMultitool
// — src/AweMultitool/Core/Formats/Canvas/CanvasThumbnailer.cs.
//   Reshaped to take raw RGBA rather than that project's CanvasImage, so one scaler serves every
//   decoder here (DDS, classic palettized sprites, PNG). License texts are collected centrally in
//   THIRD_PARTY_LICENSES.

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>One RGBA8 image: tightly packed, four bytes per pixel, no row padding.</summary>
internal readonly record struct RgbaThumbnail(int Width, int Height, byte[] Rgba);

/// <summary>
///     Scales a decoded image to fit a square gallery cell.
///     <para>
///         Two rules, each chosen for what these games actually ship. Small images scale up by a
///         WHOLE NUMBER only, because the classic catalogue is pixel art — Daggerfall's TEXTURE
///         records and Arena's IMG/CIF frames are often well under a cell, and any fractional or
///         filtered scale turns them to mush. Large images average down with the ALPHA AS A WEIGHT,
///         because palettized sources leave a key colour in RGB where they zero alpha, so an
///         unweighted average drags that colour into the visible edge and haloes every sprite.
///     </para>
/// </summary>
internal static class ThumbnailScaler
{
    /// <summary>Bytes per pixel in every buffer this class reads or writes.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>
    ///     Scales an image so its longest side fits <paramref name="cellPixels" />. Returns the
    ///     input buffer unchanged when it already fits exactly or when an upscale would not reach a
    ///     whole factor — callers must not assume they own a fresh array.
    /// </summary>
    public static RgbaThumbnail Fit(RgbaThumbnail image, int cellPixels)
    {
        ArgumentNullException.ThrowIfNull(image.Rgba);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(image.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(image.Height);

        var required = (long)image.Width * image.Height * BytesPerPixel;
        if (image.Rgba.Length < required)
        {
            throw new ArgumentException(
                $"A {image.Width}x{image.Height} RGBA image needs {required} bytes; got {image.Rgba.Length}.",
                nameof(image));
        }

        var longest = Math.Max(image.Width, image.Height);
        if (longest == cellPixels)
        {
            return image;
        }

        if (longest < cellPixels)
        {
            var factor = cellPixels / longest;
            return factor <= 1 ? image : UpscaleNearest(image, factor);
        }

        var scale = (double)cellPixels / longest;
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        return DownscaleBox(image, width, height);
    }

    /// <summary>Replicates each pixel <paramref name="factor" /> times in both directions.</summary>
    private static RgbaThumbnail UpscaleNearest(RgbaThumbnail image, int factor)
    {
        var width = image.Width * factor;
        var height = image.Height * factor;
        var output = new byte[width * height * BytesPerPixel];

        for (var y = 0; y < height; y++)
        {
            var sourceRow = y / factor * image.Width * BytesPerPixel;
            var targetRow = y * width * BytesPerPixel;

            for (var x = 0; x < width; x++)
            {
                var source = sourceRow + x / factor * BytesPerPixel;
                var target = targetRow + x * BytesPerPixel;
                output[target] = image.Rgba[source];
                output[target + 1] = image.Rgba[source + 1];
                output[target + 2] = image.Rgba[source + 2];
                output[target + 3] = image.Rgba[source + 3];
            }
        }

        return new RgbaThumbnail(width, height, output);
    }

    /// <summary>Averages each destination pixel over its source box, weighting colour by alpha.</summary>
    private static RgbaThumbnail DownscaleBox(RgbaThumbnail image, int width, int height)
    {
        var output = new byte[width * height * BytesPerPixel];

        for (var y = 0; y < height; y++)
        {
            var sourceTop = y * image.Height / height;
            var sourceBottom = Math.Max(sourceTop + 1, (y + 1) * image.Height / height);

            for (var x = 0; x < width; x++)
            {
                var sourceLeft = x * image.Width / width;
                var sourceRight = Math.Max(sourceLeft + 1, (x + 1) * image.Width / width);

                Accumulate(
                    image, sourceLeft, sourceTop, sourceRight, sourceBottom,
                    out var red, out var green, out var blue, out var alpha, out var count);

                var target = (y * width + x) * BytesPerPixel;

                // Dividing colour by the alpha total rather than the pixel count is what keeps a
                // fully transparent neighbour from voting for its key colour.
                output[target] = alpha > 0 ? (byte)(red / alpha) : (byte)0;
                output[target + 1] = alpha > 0 ? (byte)(green / alpha) : (byte)0;
                output[target + 2] = alpha > 0 ? (byte)(blue / alpha) : (byte)0;
                output[target + 3] = (byte)(alpha / count);
            }
        }

        return new RgbaThumbnail(width, height, output);
    }

    private static void Accumulate(
        RgbaThumbnail image, int left, int top, int right, int bottom,
        out long red, out long green, out long blue, out long alpha, out int count)
    {
        red = green = blue = alpha = 0;
        count = 0;

        for (var y = top; y < bottom; y++)
        {
            var row = y * image.Width * BytesPerPixel;
            for (var x = left; x < right; x++)
            {
                var source = row + x * BytesPerPixel;
                var a = image.Rgba[source + 3];

                red += image.Rgba[source] * a;
                green += image.Rgba[source + 1] * a;
                blue += image.Rgba[source + 2] * a;
                alpha += a;
                count++;
            }
        }
    }
}
