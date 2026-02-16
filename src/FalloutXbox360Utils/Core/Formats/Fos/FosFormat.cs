using System.Text;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Fos;

/// <summary>
///     Fallout 3 / New Vegas save file format module.
///     Detects "FO3SAVEGAME" magic and parses the save header.
/// </summary>
/// <remarks>
///     Save files use the FO3SAVEGAME signature for both Fallout 3 (.fos) and New Vegas (.fos/.fxs).
///     Xbox 360 saves are normally wrapped in CON/STFS packages, but the inner data
///     uses the same format. During a crash-while-saving, the raw serialization buffer
///     may be in memory with this magic at the start.
///
///     Header structure (little-endian throughout):
///     - 0x00: "FO3SAVEGAME" (11 bytes)
///     - 0x0B: Header size (uint32) — screenshot data starts at offset 4 + headerSize
///     - 0x0F: Unknown/version (uint32)
///     - 0x13: Divider '|' (0x7C)
///     - 0x14: Width (uint32) for FO3; for New Vegas, a large value followed by
///             60 extra bytes, then divider + actual width
///     - Then: divider, height, divider, save index, divider,
///             player name (len-prefixed), karma, level, location, playtime — all pipe-delimited
///     - Screenshot: raw RGB bytes (width × height × 3) at offset 4 + headerSize.
///       Xbox screenshots have swizzled channels that need reordering + per-channel row shifting.
///
///     PDB types: BGSSaveLoadFileEntry (124 bytes), SavedPlayerData (272 bytes),
///     TESSaveLoadGame (456 bytes) with EndianConvert support.
/// </remarks>
public sealed class FosFormat : FileFormatBase
{
    private static readonly byte[] Magic = "FO3SAVEGAME"u8.ToArray();
    private const byte Divider = 0x7C; // '|'
    private const int MagicLen = 11;
    private const int MinHeaderLen = 30; // enough to read magic + headerSize + first few fields

    public override string FormatId => "fos";
    public override string DisplayName => "Save";
    public override string Extension => ".fos";
    public override FileCategory Category => FileCategory.SaveGame;
    public override string OutputFolder => "saves";
    public override int MinSize => 100;
    public override int MaxSize => 20 * 1024 * 1024; // 20 MB — typical saves are 5-15 MB

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "fos",
            MagicBytes = "FO3SAVEGAME"u8.ToArray(),
            Description = "Fallout 3/New Vegas save file"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + MinHeaderLen)
        {
            return null;
        }

        // Verify magic
        if (!data.Slice(offset, MagicLen).SequenceEqual(Magic))
        {
            return null;
        }

        try
        {
            // Header size — screenshot starts at absolute offset (4 + headerSize)
            var headerSize = BinaryUtils.ReadUInt32LE(data, offset + 11);
            if (headerSize == 0 || headerSize > 10_000)
            {
                return null;
            }

            // Verify first divider
            if (data.Length < offset + 24 || data[offset + 19] != Divider)
            {
                return null;
            }

            // Read the value after the first divider — width for FO3, large value for NV
            var val = BinaryUtils.ReadUInt32LE(data, offset + 20);

            uint width;
            uint height;
            bool isNewVegas;
            int fieldPos; // tracks position for parsing remaining header fields

            if (val > 16384)
            {
                // New Vegas: 60 extra bytes, then divider + width + divider + height
                isNewVegas = true;
                if (data.Length < offset + 95)
                {
                    return null;
                }

                if (data[offset + 84] != Divider)
                {
                    return null;
                }

                width = BinaryUtils.ReadUInt32LE(data, offset + 85);

                if (data[offset + 89] != Divider)
                {
                    return null;
                }

                height = BinaryUtils.ReadUInt32LE(data, offset + 90);
                fieldPos = offset + 94; // after height
            }
            else
            {
                // Fallout 3: val IS the width
                isNewVegas = false;
                width = val;

                if (data[offset + 24] != Divider)
                {
                    return null;
                }

                height = BinaryUtils.ReadUInt32LE(data, offset + 25);
                fieldPos = offset + 29; // after height
            }

            // Validate screenshot dimensions
            if (width == 0 || height == 0 || width > 4096 || height > 4096)
            {
                return null;
            }

            // Screenshot location and size
            var screenshotOffset = 4 + (int)headerSize;
            var screenshotSize = (int)(width * height * 3);
            var estimatedSize = screenshotOffset + screenshotSize;

            // Sanity: screenshot offset should be within reasonable range
            if (screenshotOffset < MagicLen + 4 || screenshotOffset > 10_000)
            {
                return null;
            }

            var metadata = new Dictionary<string, object>
            {
                ["width"] = (int)width,
                ["height"] = (int)height,
                ["screenshotOffset"] = screenshotOffset,
                ["screenshotSize"] = screenshotSize,
                ["game"] = isNewVegas ? "Fallout: New Vegas" : "Fallout 3",
                ["dimensions"] = $"{width}x{height}"
            };

            // Parse remaining pipe-delimited header fields for metadata
            TryParseHeaderFields(data, fieldPos, metadata);

            var gameName = isNewVegas ? "FNV" : "FO3";
            var playerName = metadata.TryGetValue("playerName", out var name) ? $" - {name}" : "";

            return new ParseResult
            {
                Format = $"{gameName} Save",
                EstimatedSize = estimatedSize,
                Metadata = metadata,
                FileName = metadata.TryGetValue("safeName", out var safe) ? (string)safe : null
            };
        }
        catch
        {
            return null;
        }
    }

    public override string GetDisplayDescription(string signatureId,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        if (metadata is null)
        {
            return "Fallout save file";
        }

        var parts = new List<string>();

        if (metadata.TryGetValue("game", out var game))
        {
            parts.Add((string)game);
        }

        if (metadata.TryGetValue("playerName", out var name))
        {
            parts.Add($"\"{name}\"");
        }

        if (metadata.TryGetValue("playerLevel", out var level))
        {
            parts.Add($"Lv.{level}");
        }

        if (metadata.TryGetValue("playerLocation", out var loc))
        {
            parts.Add((string)loc);
        }

        if (metadata.TryGetValue("dimensions", out var dims))
        {
            parts.Add($"screenshot {dims}");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "Fallout save file";
    }

    /// <summary>
    ///     Attempts to parse the pipe-delimited header fields after width/height.
    ///     Fields: save_index | name_len | name | karma_len | karma | level | loc_len | loc | time_len | time
    /// </summary>
    private static void TryParseHeaderFields(ReadOnlySpan<byte> data, int pos, Dictionary<string, object> metadata)
    {
        // save_index: | uint32
        if (!TryReadDividerAndUInt32(data, ref pos, out var saveIndex))
        {
            return;
        }

        metadata["saveIndex"] = (int)saveIndex;

        // player name: | uint16 len | string
        if (!TryReadDividerAndString(data, ref pos, out var playerName))
        {
            return;
        }

        if (!string.IsNullOrEmpty(playerName))
        {
            metadata["playerName"] = playerName;
            metadata["safeName"] = SanitizeFileName(playerName, saveIndex);
        }

        // karma: | uint16 len | string
        if (!TryReadDividerAndString(data, ref pos, out var karma))
        {
            return;
        }

        if (!string.IsNullOrEmpty(karma))
        {
            metadata["playerKarma"] = karma;
        }

        // level: | uint32
        if (!TryReadDividerAndUInt32(data, ref pos, out var level))
        {
            return;
        }

        metadata["playerLevel"] = (int)level;

        // location: | uint16 len | string
        if (!TryReadDividerAndString(data, ref pos, out var location))
        {
            return;
        }

        if (!string.IsNullOrEmpty(location))
        {
            metadata["playerLocation"] = location;
        }

        // playtime: | uint16 len | string
        if (!TryReadDividerAndString(data, ref pos, out var playtime))
        {
            return;
        }

        if (!string.IsNullOrEmpty(playtime))
        {
            metadata["playtime"] = playtime;
        }
    }

    private static bool TryReadDividerAndUInt32(ReadOnlySpan<byte> data, ref int pos, out uint value)
    {
        value = 0;
        if (pos + 5 > data.Length || data[pos] != Divider)
        {
            return false;
        }

        pos++; // skip divider
        value = BinaryUtils.ReadUInt32LE(data, pos);
        pos += 4;
        return true;
    }

    private static bool TryReadDividerAndString(ReadOnlySpan<byte> data, ref int pos, out string value)
    {
        value = "";

        // Read divider + uint16 length
        if (pos + 3 > data.Length || data[pos] != Divider)
        {
            return false;
        }

        pos++; // skip divider
        var len = BinaryUtils.ReadUInt16LE(data, pos);
        pos += 2;

        if (len > 1024)
        {
            return false; // sanity check
        }

        // Read divider + string data
        if (pos + 1 + len > data.Length || data[pos] != Divider)
        {
            return false;
        }

        pos++; // skip divider
        if (len > 0)
        {
            value = Encoding.Latin1.GetString(data.Slice(pos, len));
            pos += len;
        }

        return true;
    }

    private static string SanitizeFileName(string playerName, uint saveIndex)
    {
        var sb = new StringBuilder();
        sb.Append($"save_{saveIndex:D3}_");
        foreach (var ch in playerName)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                sb.Append(ch);
            }
            else if (ch == ' ')
            {
                sb.Append('_');
            }
        }

        return sb.ToString().TrimEnd('_');
    }
}
