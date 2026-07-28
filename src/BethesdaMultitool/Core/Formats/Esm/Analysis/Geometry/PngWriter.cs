using ImageMagick;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;

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
        WritePngViaStream(image, path);
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
        WritePngViaStream(image, path);
    }

    /// <summary>
    ///     Saves an RGBA image (32-bit) to PNG, streaming straight to the file — no intermediate
    ///     in-memory PNG copy (use <see cref="EncodeRgba" /> only when the bytes are consumed in memory).
    /// </summary>
    public static void SaveRgba(byte[] pixels, int width, int height, string path)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = MagickFormat.Rgba,
            Depth = 8
        };

        using var image = new MagickImage(pixels, settings);
        WritePngViaStream(image, path);
    }

    /// <summary>
    ///     PNG encode tuning: zlib level 2 + Sub row filter (filter 1) instead of ImageMagick's default
    ///     quality 75 = zlib level 7 + adaptive filtering (which trials all five row filters per row) —
    ///     the encode was the wall-clock bottleneck of every render/export PNG save. Several times
    ///     faster to encode for modestly larger files; output stays 8-bit (Depth pinned by the read
    ///     settings) and pixel-identical on decode — only the compression container changes.
    /// </summary>
    private static void ApplyFastEncodeSettings(MagickImage image)
    {
        image.Settings.SetDefine(MagickFormat.Png, "compression-level", "2");
        image.Settings.SetDefine(MagickFormat.Png, "compression-filter", "1");
        // Raw renderer/export pixels have no source metadata to preserve. ImageMagick otherwise
        // injects wall-clock date:create/date:modify text chunks at write time, making two PNGs with
        // byte-identical pixels hash differently solely because they were encoded at different times.
        image.Settings.SetDefine(MagickFormat.Png, "exclude-chunk", "date,time");
    }

    // Magick.NET's path-based Write goes through native fopen, which fails on Windows paths >= MAX_PATH (260).
    // Routing through FileStream lets .NET 6+ apply its long-path handling transparently.
    private static void WritePngViaStream(MagickImage image, string path)
    {
        ApplyFastEncodeSettings(image);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        image.Write(stream, MagickFormat.Png);
    }

    /// <summary>Encodes raw RGBA pixel data to PNG bytes.</summary>
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
        ApplyFastEncodeSettings(image);
        using var stream = new MemoryStream();
        image.Write(stream, MagickFormat.Png);
        return stream.ToArray();
    }
}
