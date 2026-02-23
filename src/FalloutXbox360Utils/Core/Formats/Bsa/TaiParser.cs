using System.Globalization;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Bsa;

/// <summary>
///     A single entry in a Bethesda Texture Atlas Index (.tai) file.
///     UV coordinates are normalized [0,1] relative to the atlas dimensions.
/// </summary>
public readonly record struct TaiEntry(
    string VirtualPath,
    string AtlasName,
    int AtlasIndex,
    float UOffset,
    float VOffset,
    float UWidth,
    float VHeight)
{
    /// <summary>The filename portion of the virtual path (without extension).</summary>
    public string Name => Path.GetFileNameWithoutExtension(VirtualPath);

    /// <summary>Convert UV coordinates to pixel rectangle given atlas dimensions.</summary>
    public (int X, int Y, int Width, int Height) ToPixelRect(int atlasWidth, int atlasHeight)
    {
        return ((int)(UOffset * atlasWidth),
            (int)(VOffset * atlasHeight),
            (int)(UWidth * atlasWidth),
            (int)(VHeight * atlasHeight));
    }
}

/// <summary>
///     Parser for Bethesda Texture Atlas Index (.tai) files.
///     TAI files are simple text files where each line maps a virtual texture path
///     to a rectangle within a DDS atlas texture using normalized UV coordinates.
/// </summary>
public static class TaiParser
{
    /// <summary>
    ///     Parse a TAI file from raw bytes (may be UTF-8 or ASCII).
    /// </summary>
    public static IReadOnlyList<TaiEntry> Parse(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        return Parse(text);
    }

    /// <summary>
    ///     Parse a TAI file from text content.
    /// </summary>
    public static IReadOnlyList<TaiEntry> Parse(string text)
    {
        var entries = new List<TaiEntry>();
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            // TAI format: <virtual_path>\t<atlas_name>\t<index>\t<u>\t<v>\t<u_width>\t<v_height>
            var parts = trimmed.Split('\t');
            if (parts.Length < 7)
                continue;

            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                continue;
            if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var u))
                continue;
            if (!float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                continue;
            if (!float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var uWidth))
                continue;
            if (!float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var vHeight))
                continue;

            entries.Add(new TaiEntry(parts[0].Trim(), parts[1].Trim(), index, u, v, uWidth, vHeight));
        }

        return entries;
    }
}
