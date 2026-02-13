using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Exports extracted NiPixelData textures as DDS files.
///     Synthesizes DDS headers and handles Xbox 360 tile untiling + endian swap.
/// </summary>
public static class DdsExporter
{
    #region DDS Constants

    private const uint DdsMagic = 0x20534444; // "DDS "
    private const uint DdsHeaderSize = 124;
    private const uint DdsPixelFormatSize = 32;

    // DDS header flags
    private const uint DdsdCaps = 0x1;
    private const uint DdsdHeight = 0x2;
    private const uint DdsdWidth = 0x4;
    private const uint DdsdPixelFormat = 0x1000;
    private const uint DdsdMipmapCount = 0x20000;
    private const uint DdsdLinearSize = 0x80000;

    // Pixel format flags
    private const uint DdpfFourCc = 0x4;
    private const uint DdpfRgb = 0x40;
    private const uint DdpfAlphaPixels = 0x1;

    // Caps flags
    private const uint DdsCapsTexture = 0x1000;
    private const uint DdsCapsMipmap = 0x400000;
    private const uint DdsCapsComplex = 0x8;

    // Caps2 flags (cubemaps)
    private const uint DdsCaps2Cubemap = 0x200;
    private const uint DdsCaps2CubemapAllFaces = 0xFC00; // +X, -X, +Y, -Y, +Z, -Z

    // FourCC codes (little-endian ASCII)
    private const uint FourCcDxt1 = 0x31545844; // "DXT1"
    private const uint FourCcDxt3 = 0x33545844; // "DXT3"
    private const uint FourCcDxt5 = 0x35545844; // "DXT5"

    #endregion

    /// <summary>
    ///     Export a single texture as a DDS file.
    ///     Handles Xbox 360 texture untiling and endian swap for compressed formats.
    /// </summary>
    public static void Export(ExtractedTexture texture, string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fs = File.Create(outputPath);
        using var writer = new BinaryWriter(fs);

        WriteHeader(writer, texture);

        var pixelData = ProcessPixelData(texture);
        writer.Write(pixelData);
    }

    /// <summary>
    ///     Export all textures to a directory, plus a summary CSV.
    /// </summary>
    public static void ExportAll(
        IReadOnlyList<ExtractedTexture> textures,
        string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        for (var i = 0; i < textures.Count; i++)
        {
            var texture = textures[i];
            var name = GetTextureFileName(texture, i);
            Export(texture, Path.Combine(outputDir, name));
        }

        ExportSummary(textures, Path.Combine(outputDir, "texture_summary.csv"));
    }

    /// <summary>
    ///     Export a summary CSV of all extracted textures.
    /// </summary>
    public static void ExportSummary(IReadOnlyList<ExtractedTexture> textures, string outputPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Index,Offset,Format,Width,Height,MipLevels,BPP,Faces,DataSize,Filename");

        for (var i = 0; i < textures.Count; i++)
        {
            var t = textures[i];
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{i},0x{t.SourceOffset:X},{t.Format},{t.Width},{t.Height}," +
                $"{t.MipmapLevels},{t.BitsPerPixel},{t.Faces},{t.DataSize}," +
                $"{CsvEscape(t.Filename)}");
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outputPath, sb.ToString());
    }

    /// <summary>
    ///     Write the DDS file header (magic + header + pixel format = 128 bytes).
    /// </summary>
    private static void WriteHeader(BinaryWriter writer, ExtractedTexture texture)
    {
        // DDS magic
        writer.Write(DdsMagic);

        // DDS_HEADER.dwSize
        writer.Write(DdsHeaderSize);

        // DDS_HEADER.dwFlags
        var flags = DdsdCaps | DdsdHeight | DdsdWidth | DdsdPixelFormat;
        if (texture.MipmapLevels > 1)
        {
            flags |= DdsdMipmapCount;
        }

        if (texture.IsCompressed)
        {
            flags |= DdsdLinearSize;
        }

        writer.Write(flags);

        // DDS_HEADER.dwHeight, dwWidth
        writer.Write((uint)texture.Height);
        writer.Write((uint)texture.Width);

        // DDS_HEADER.dwPitchOrLinearSize
        var pitch = CalculatePitch(texture);
        writer.Write(pitch);

        // DDS_HEADER.dwDepth
        writer.Write(0u);

        // DDS_HEADER.dwMipMapCount
        writer.Write((uint)texture.MipmapLevels);

        // DDS_HEADER.dwReserved1[11]
        for (var i = 0; i < 11; i++)
        {
            writer.Write(0u);
        }

        // DDS_PIXELFORMAT
        WritePixelFormat(writer, texture);

        // DDS_HEADER.dwCaps
        var caps = DdsCapsTexture;
        if (texture.MipmapLevels > 1 || texture.IsCubemap)
        {
            caps |= DdsCapsComplex;
        }

        if (texture.MipmapLevels > 1)
        {
            caps |= DdsCapsMipmap;
        }

        writer.Write(caps);

        // DDS_HEADER.dwCaps2 — cubemap flags
        var caps2 = 0u;
        if (texture.IsCubemap)
        {
            caps2 = DdsCaps2Cubemap | DdsCaps2CubemapAllFaces;
        }

        writer.Write(caps2);

        // dwCaps3, dwCaps4, dwReserved2
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
    }

    /// <summary>
    ///     Write the DDS_PIXELFORMAT struct (32 bytes).
    /// </summary>
    private static void WritePixelFormat(BinaryWriter writer, ExtractedTexture texture)
    {
        writer.Write(DdsPixelFormatSize); // dwSize

        if (texture.IsCompressed)
        {
            // Compressed: use FourCC
            writer.Write(DdpfFourCc); // dwFlags
            writer.Write(GetFourCc(texture.Format)); // dwFourCC
            writer.Write(0u); // dwRGBBitCount
            writer.Write(0u); // dwRBitMask
            writer.Write(0u); // dwGBitMask
            writer.Write(0u); // dwBBitMask
            writer.Write(0u); // dwABitMask
        }
        else if (texture.Format == NiTextureFormat.RGBA)
        {
            // 32-bit RGBA
            writer.Write(DdpfRgb | DdpfAlphaPixels);
            writer.Write(0u); // dwFourCC
            writer.Write(32u); // dwRGBBitCount
            writer.Write(0x00FF0000u); // R mask
            writer.Write(0x0000FF00u); // G mask
            writer.Write(0x000000FFu); // B mask
            writer.Write(0xFF000000u); // A mask
        }
        else
        {
            // Uncompressed RGB (use inferred BPP — runtime value is often wrong)
            var bpp = (uint)texture.EffectiveBitsPerPixel;
            writer.Write(DdpfRgb);
            writer.Write(0u);
            writer.Write(bpp);
            if (bpp == 16)
            {
                // R5G6B5
                writer.Write(0x0000F800u); // R mask
                writer.Write(0x000007E0u); // G mask
                writer.Write(0x0000001Fu); // B mask
            }
            else
            {
                // 24-bit or 32-bit RGB
                writer.Write(0x00FF0000u); // R mask
                writer.Write(0x0000FF00u); // G mask
                writer.Write(0x000000FFu); // B mask
            }

            writer.Write(0u); // A mask
        }
    }

    /// <summary>
    ///     Process pixel data for DDS export.
    ///     NiPixelData is CPU-side source data stored in LINEAR layout (not Morton-tiled).
    ///     Xbox 360 big-endian format requires 16-bit word swap for all DXT blocks and
    ///     appropriate swapping for uncompressed pixel data.
    /// </summary>
    private static byte[] ProcessPixelData(ExtractedTexture texture)
    {
        if (!texture.IsCompressed)
        {
            return SwapUncompressedEndian(texture);
        }

        // NiPixelData stores pixel data in the original source file format.
        // These textures come from DDS files (not DDX), so data is already little-endian.
        // No byte-swapping or untiling needed — write raw data directly.
        return texture.PixelData;
    }

    /// <summary>
    ///     Byte-swap uncompressed texture data (Xbox 360 is big-endian).
    /// </summary>
    private static byte[] SwapUncompressedEndian(ExtractedTexture texture)
    {
        var data = new byte[texture.PixelData.Length];
        Array.Copy(texture.PixelData, data, data.Length);

        var bpp = texture.EffectiveBitsPerPixel;
        switch (bpp)
        {
            case 16:
                // Swap pairs of bytes (R5G6B5 etc.)
                for (var i = 0; i < data.Length - 1; i += 2)
                {
                    (data[i], data[i + 1]) = (data[i + 1], data[i]);
                }

                break;
            case 32:
                // Swap 4-byte groups (RGBA8888)
                for (var i = 0; i < data.Length - 3; i += 4)
                {
                    (data[i], data[i + 3]) = (data[i + 3], data[i]);
                    (data[i + 1], data[i + 2]) = (data[i + 2], data[i + 1]);
                }

                break;
            // 8bpp and 24bpp: no swap needed (bytes are byte-order independent)
        }

        return data;
    }

    private static uint GetFourCc(NiTextureFormat format) => format switch
    {
        NiTextureFormat.DXT1 => FourCcDxt1,
        NiTextureFormat.DXT3 => FourCcDxt3,
        NiTextureFormat.DXT5 => FourCcDxt5,
        _ => FourCcDxt5
    };

    private static uint CalculatePitch(ExtractedTexture texture)
    {
        if (texture.IsCompressed)
        {
            var blockSize = texture.Format == NiTextureFormat.DXT1 ? 8u : 16u;
            return Math.Max(1, (uint)(texture.Width + 3) / 4) * blockSize;
        }

        return (uint)(texture.Width * texture.EffectiveBitsPerPixel / 8);
    }

    private static string GetTextureFileName(ExtractedTexture texture, int index)
    {
        if (texture.Filename != null)
        {
            var safeName = SanitizeFileName(Path.GetFileNameWithoutExtension(texture.Filename));
            return $"tex_{index:D4}_{safeName}.dds";
        }

        return $"tex_{index:D4}_{texture.SourceOffset:X}_{texture.Width}x{texture.Height}_{texture.Format}.dds";
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }

        var result = sb.ToString();
        return result.Length > 60 ? result[..60] : result;
    }

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
