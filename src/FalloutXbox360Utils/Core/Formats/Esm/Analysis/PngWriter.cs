using ImageMagick;

namespace FalloutXbox360Utils.Core.Formats.Esm.Analysis;

/// <summary>
///     Simple PNG writer using Magick.NET (replaces ImageSharp dependency).
/// </summary>
public static class PngWriter
{
    /// <summary>
    ///     Saves a grayscale image (8-bit) to PNG.
    /// </summary>
    public static void SaveGrayscale(byte[] pixels, int width, int height, string path)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = MagickFormat.Gray,
            Depth = 8
        };

        using var image = new MagickImage(pixels, settings);
        image.Write(path, MagickFormat.Png);
    }

    /// <summary>
    ///     Saves an RGB image (24-bit) to PNG.
    /// </summary>
    public static void SaveRgb(byte[] pixels, int width, int height, string path)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = MagickFormat.Rgb,
            Depth = 8
        };

        using var image = new MagickImage(pixels, settings);
        image.Write(path, MagickFormat.Png);
    }

    /// <summary>
    ///     Saves an RGBA image (32-bit) to PNG.
    /// </summary>
    public static void SaveRgba(byte[] pixels, int width, int height, string path)
    {
        var pngBytes = EncodeRgba(pixels, width, height);
        File.WriteAllBytes(path, pngBytes);
    }

    public static byte[] EncodeRgba(byte[] pixels, int width, int height)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = MagickFormat.Rgba,
            Depth = 8
        };

        using var image = new MagickImage(pixels, settings);
        using var stream = new MemoryStream();
        image.Write(stream, MagickFormat.Png);
        return stream.ToArray();
    }
}
