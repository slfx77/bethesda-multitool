using System.Text;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Parser for Fallout 3/NV save files (FO3SAVEGAME format).
///     Handles both raw .fos files and .fxs files wrapped in Xbox 360 STFS containers.
/// </summary>
public static class SaveFileParser
{
    private const byte PipeTerminator = 0x7C;
    private static readonly byte[] Magic = "FO3SAVEGAME"u8.ToArray();

    /// <summary>
    ///     Parse a save file from raw bytes (may be STFS-wrapped or raw FO3SAVEGAME).
    /// </summary>
    public static SaveFile Parse(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> payload;
        var stfsPayloadOffset = 0;
        StfsExtractionResult? stfsResult = null;
        int? exactPayloadSize = null;

        // Check for raw FO3SAVEGAME first (not STFS-wrapped)
        if (data.Length >= Magic.Length && data[..Magic.Length].SequenceEqual(Magic))
        {
            payload = data;
        }
        else
        {
            // Try STFS container extraction
            stfsResult = StfsContainer.TryExtract(data);
            if (stfsResult.Success)
            {
                payload = stfsResult.Payload;
                stfsPayloadOffset = stfsResult.FileEntry?.StartBlock > 0
                    ? StfsContainer.DataBlockToRawOffset(stfsResult.FileEntry.StartBlock)
                    : -1;
                exactPayloadSize = stfsResult.FileEntry?.FileSize;
            }
            else
            {
                throw new InvalidDataException(
                    $"Not a valid save file. {stfsResult.DiagnosticSummary}");
            }
        }

        var header = ParseHeader(payload, out var headerEnd, exactPayloadSize);
        var locationTable = ParseFileLocationTable(payload, ref headerEnd);

        // FLT offsets are absolute from save payload start
        var bodyBase = 0;

        var globalData1 = ParseGlobalDataEntries(payload, bodyBase, locationTable.GlobalDataTable1Offset,
            locationTable.GlobalDataTable1Count);
        var changedForms = ParseChangedForms(payload, bodyBase, locationTable.ChangedFormsOffset,
            locationTable.ChangedFormsCount);
        var globalData2 = ParseGlobalDataEntries(payload, bodyBase, locationTable.GlobalDataTable2Offset,
            locationTable.GlobalDataTable2Count);

        // Parse FormID array
        var formIdArrayOffset = bodyBase + (int)locationTable.RefIdArrayCountOffset;
        var formIdArray = ParseFormIdArray(payload, formIdArrayOffset, out var afterFormIds);

        // Parse visited worldspaces
        var visitedWorldspaces = ParseFormIdArray(payload, afterFormIds, out _);

        // Decode global data types
        PlayerLocation? playerLocation = null;
        var globalVariables = new List<GlobalVariable>();
        SaveStatistics statistics = new();
        foreach (var entry in globalData1)
        {
            switch (entry.Type)
            {
                case 0:
                    statistics = ParseMiscStats(entry.Data);
                    break;
                case 1 when entry.Data.Length >= 19:
                    playerLocation = ParsePlayerLocation(entry.Data);
                    break;
                case 3:
                    globalVariables.AddRange(ParseGlobalVariables(entry.Data));
                    break;
            }
        }

        return new SaveFile
        {
            Header = header,
            Statistics = statistics,
            LocationTable = locationTable,
            GlobalData1 = globalData1,
            GlobalData2 = globalData2,
            ChangedForms = changedForms,
            FormIdArray = formIdArray,
            VisitedWorldspaces = visitedWorldspaces,
            PlayerLocation = playerLocation,
            GlobalVariables = globalVariables,
            StfsPayloadOffset = stfsPayloadOffset,
            StfsExtractionMethod = stfsResult?.Method.ToString()
        };
    }

    /// <summary>
    ///     Finds the offset of the FO3SAVEGAME magic within the data.
    ///     Returns 0 if the data starts with the magic (raw .fos file),
    ///     or the STFS container offset for .fxs files.
    /// </summary>
    public static int FindMagicOffset(ReadOnlySpan<byte> data)
    {
        if (data.Length >= Magic.Length && data[..Magic.Length].SequenceEqual(Magic))
        {
            return 0;
        }

        // Try STFS extraction first
        var stfs = StfsContainer.TryExtract(data);
        if (stfs.Success && stfs.FileEntry != null)
        {
            return StfsContainer.DataBlockToRawOffset(stfs.FileEntry.StartBlock);
        }

        // Fallback: search for FO3SAVEGAME magic at block boundaries
        for (var offset = 0x1000; offset < data.Length - Magic.Length; offset += 0x1000)
        {
            if (data.Slice(offset, Magic.Length).SequenceEqual(Magic))
            {
                return offset;
            }
        }

        // Last resort: brute-force scan
        return data.IndexOf(Magic.AsSpan());
    }

    /// <summary>Kept for backward compatibility.</summary>
    public static int FindPayloadOffset(ReadOnlySpan<byte> data)
    {
        return FindMagicOffset(data);
    }

    /// <summary>
    ///     Extract the save payload from STFS data.
    ///     Uses StfsContainer for proper extraction with file table support.
    /// </summary>
    public static byte[] ExtractPayload(ReadOnlySpan<byte> data, int magicOffset)
    {
        if (magicOffset == 0)
        {
            return data.ToArray(); // Raw .fos file, no STFS wrapper
        }

        // Delegate to StfsContainer for proper STFS extraction
        var stfs = StfsContainer.TryExtract(data);
        if (stfs.Success)
        {
            return stfs.Payload!;
        }

        // Fallback: read from magic offset to end
        return data[magicOffset..].ToArray();
    }

    /// <summary>
    ///     Parse the save file header (everything before the body).
    ///     The File Location Table immediately follows the plugin list (no stats section in between).
    /// </summary>
    /// <param name="data">The save payload starting from FO3SAVEGAME magic.</param>
    /// <param name="position">Set to the position after the FLT on return.</param>
    /// <param name="payloadSize">Exact payload size from STFS file entry (bounds formVersion search).</param>
    private static SaveFileHeader ParseHeader(ReadOnlySpan<byte> data, out int position, int? payloadSize = null)
    {
        // Magic: "FO3SAVEGAME" (11 bytes, no null terminator in these prototype builds)
        position = Magic.Length;

        // Header Size (uint32)
        var headerSize = BinaryUtils.ReadUInt32LE(data, position);
        position += 4;

        var headerStart = position;

        // Parse pipe-terminated fields within the header
        var version = ReadUInt32T(data, ref position);
        var screenshotWidth = ReadUInt32T(data, ref position);
        var screenshotHeight = ReadUInt32T(data, ref position);
        var saveNumber = ReadUInt32T(data, ref position);
        var playerName = ReadLenStringT(data, ref position);
        var playerStatus = ReadLenStringT(data, ref position);
        var playerLevel = ReadUInt32T(data, ref position);
        var playerCell = ReadLenStringT(data, ref position);
        var saveDuration = ReadLenStringT(data, ref position);

        // Skip to end of header
        var headerEnd = headerStart + (int)headerSize;
        if (position < headerEnd)
        {
            position = headerEnd;
        }

        // Screenshot data - try standard 3bpp first, then search for formVersion marker
        var screenshotDataOffset = position;
        var screenshotDataSize = (int)(screenshotWidth * screenshotHeight * 3);
        byte formVersion = 0;
        uint pluginInfoSize = 0;

        // Bound the search to the actual payload size when known
        var maxSearchEnd = payloadSize.HasValue
            ? Math.Min(payloadSize.Value, data.Length)
            : data.Length;

        // Try 3bpp and 4bpp screenshot sizes
        var found = false;
        foreach (var bpp in new[] { 3, 4 })
        {
            var trySize = (int)(screenshotWidth * screenshotHeight * bpp);
            var fvPos = headerEnd + trySize;
            if (fvPos + 5 < maxSearchEnd)
            {
                var fv = data[fvPos];
                var piSize = BinaryUtils.ReadUInt32LE(data, fvPos + 1);
                if (fv is >= 19 and <= 22 && piSize < 1000 &&
                    ValidateFormVersionCandidate(data, fvPos + 5 + (int)piSize, maxSearchEnd))
                {
                    screenshotDataSize = trySize;
                    formVersion = fv;
                    pluginInfoSize = piSize;
                    position = fvPos + 5;
                    found = true;
                    break;
                }
            }
        }

        // Fallback: search for formVersion marker validated by plugin structure (.esm/.esp) AND FLT
        if (!found)
        {
            var searchEnd = Math.Min(headerEnd + (int)(screenshotWidth * screenshotHeight * 8), maxSearchEnd - 5);
            for (var i = headerEnd; i < searchEnd; i++)
            {
                var fvCandidate = data[i];
                if (fvCandidate is < 19 or > 22)
                {
                    continue;
                }

                var piSize = BinaryUtils.ReadUInt32LE(data, i + 1);
                if (piSize is < 1 or > 1000)
                {
                    continue;
                }

                // Validate: plugin info section should contain .esm or .esp filename
                var pluginRegionStart = i + 5;
                var pluginRegionEnd = Math.Min(pluginRegionStart + (int)piSize, maxSearchEnd);
                if (pluginRegionEnd - pluginRegionStart < 5)
                {
                    continue;
                }

                var pluginRegion = data[pluginRegionStart..pluginRegionEnd];
                if (pluginRegion.IndexOf(".esm"u8) < 0 && pluginRegion.IndexOf(".esp"u8) < 0)
                {
                    continue;
                }

                // Additional validation: check FLT at expected position produces sane offsets
                var fltPosition = pluginRegionStart + (int)piSize;
                if (!ValidateFormVersionCandidate(data, fltPosition, maxSearchEnd))
                {
                    continue;
                }

                screenshotDataSize = i - headerEnd;
                formVersion = fvCandidate;
                pluginInfoSize = piSize;
                position = i + 5;
                found = true;
                break;
            }
        }

        if (!found)
        {
            // Last resort: assume 3bpp, but validate the result
            position = headerEnd + screenshotDataSize;
            if (position + 5 < data.Length)
            {
                var fvCandidate = data[position];
                var piCandidate = BinaryUtils.ReadUInt32LE(data, position + 1);
                var fltPos = position + 5 + (int)piCandidate;

                if (piCandidate < 1000 && ValidateFormVersionCandidate(data, fltPos, maxSearchEnd))
                {
                    formVersion = fvCandidate;
                    pluginInfoSize = piCandidate;
                    position += 5;
                    found = true;
                }
            }
        }

        if (!found)
        {
            throw new InvalidDataException(
                "Unable to locate formVersion in save header. " +
                $"Screenshot {screenshotWidth}x{screenshotHeight} — no valid formVersion/FLT found at any BPP. " +
                "The save data after the header may be corrupted.");
        }

        // Plugin count (uint8) followed by pipe-separated length-prefixed plugin names
        var plugins = new List<string>();
        var pluginsEnd = position + (int)pluginInfoSize;
        if (position < data.Length)
        {
            var pluginCount = data[position];
            position++;

            // Skip pipe separator after plugin count
            if (position < data.Length && data[position] == PipeTerminator)
            {
                position++;
            }

            for (var i = 0; i < pluginCount && position < pluginsEnd; i++)
            {
                plugins.Add(ReadLenStringT(data, ref position));
            }
        }

        // FLT immediately follows plugins (no stats section in prototype builds)
        position = pluginsEnd;

        return new SaveFileHeader
        {
            HeaderSize = headerSize,
            Version = version,
            ScreenshotWidth = screenshotWidth,
            ScreenshotHeight = screenshotHeight,
            SaveNumber = saveNumber,
            PlayerName = playerName,
            PlayerStatus = playerStatus,
            PlayerLevel = playerLevel,
            PlayerCell = playerCell,
            SaveDuration = saveDuration,
            FormVersion = formVersion,
            Plugins = plugins,
            ScreenshotDataOffset = screenshotDataOffset,
            ScreenshotDataSize = screenshotDataSize
        };
    }

    /// <summary>
    ///     Validate a formVersion candidate by checking the File Location Table at the expected position.
    ///     The FLT should produce sane offsets: ChangedFormsOffset within payload, reasonable counts.
    /// </summary>
    private static bool ValidateFormVersionCandidate(ReadOnlySpan<byte> data, int fltPosition, int maxPayloadSize)
    {
        if (fltPosition + FileLocationTable.Size > data.Length)
        {
            return false;
        }

        // Read key FLT fields
        var refIdOffset = BinaryUtils.ReadUInt32LE(data, fltPosition);
        var gdt1Offset = BinaryUtils.ReadUInt32LE(data, fltPosition + 8);
        var cfOffset = BinaryUtils.ReadUInt32LE(data, fltPosition + 12);
        var gdt1Count = BinaryUtils.ReadUInt32LE(data, fltPosition + 20);
        var cfCount = BinaryUtils.ReadUInt32LE(data, fltPosition + 28);

        // Sanity checks: offsets should be within payload and in order
        if (cfOffset == 0 || cfOffset > (uint)maxPayloadSize)
        {
            return false;
        }

        if (gdt1Offset >= cfOffset)
        {
            return false; // GDT1 should come before changed forms
        }

        if (refIdOffset <= cfOffset)
        {
            return false; // RefID array should come after changed forms
        }

        if (cfCount > 100_000 || gdt1Count > 100)
        {
            return false; // Unreasonable counts
        }

        // FLT position itself should be before the GDT1 offset
        if ((uint)fltPosition > gdt1Offset)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Parse the File Location Table (110 bytes).
    /// </summary>
    private static FileLocationTable ParseFileLocationTable(ReadOnlySpan<byte> data, ref int position)
    {
        if (position < 0 || position + FileLocationTable.Size > data.Length)
        {
            throw new InvalidDataException(
                $"File Location Table at offset 0x{position:X} extends beyond payload ({data.Length} bytes). " +
                "The formVersion/screenshot detection may have failed.");
        }

        var table = new FileLocationTable
        {
            RefIdArrayCountOffset = BinaryUtils.ReadUInt32LE(data, position),
            UnknownTableOffset = BinaryUtils.ReadUInt32LE(data, position + 4),
            GlobalDataTable1Offset = BinaryUtils.ReadUInt32LE(data, position + 8),
            ChangedFormsOffset = BinaryUtils.ReadUInt32LE(data, position + 12),
            GlobalDataTable2Offset = BinaryUtils.ReadUInt32LE(data, position + 16),
            GlobalDataTable1Count = BinaryUtils.ReadUInt32LE(data, position + 20),
            GlobalDataTable2Count = BinaryUtils.ReadUInt32LE(data, position + 24),
            ChangedFormsCount = BinaryUtils.ReadUInt32LE(data, position + 28),
            UnknownCount = BinaryUtils.ReadUInt32LE(data, position + 32)
        };

        position += FileLocationTable.Size;
        return table;
    }

    /// <summary>
    ///     Parse Global Data entries from a section.
    ///     Delegates to <see cref="ChangedFormParser"/>.
    /// </summary>
    private static List<GlobalDataEntry> ParseGlobalDataEntries(ReadOnlySpan<byte> data, int bodyBase,
        uint sectionOffset, uint count)
    {
        return ChangedFormParser.ParseGlobalDataEntries(data, bodyBase, sectionOffset, count);
    }

    /// <summary>
    ///     Parse Changed Form entries from the body.
    ///     Delegates to <see cref="ChangedFormParser"/>.
    /// </summary>
    private static List<ChangedForm> ParseChangedForms(ReadOnlySpan<byte> data, int bodyBase, uint sectionOffset,
        uint count)
    {
        return ChangedFormParser.ParseChangedForms(data, bodyBase, sectionOffset, count);
    }

    /// <summary>
    ///     Parse the FormID lookup array.
    ///     Delegates to <see cref="ChangedFormParser"/>.
    /// </summary>
    private static List<uint> ParseFormIdArray(ReadOnlySpan<byte> data, int offset, out int endOffset)
    {
        return ChangedFormParser.ParseFormIdArray(data, offset, out endOffset);
    }

    /// <summary>
    ///     Parse player location from Global Data Type 1 (TES).
    ///     Delegates to <see cref="ChangedFormParser"/>.
    /// </summary>
    private static PlayerLocation? ParsePlayerLocation(ReadOnlySpan<byte> data)
    {
        return ChangedFormParser.ParsePlayerLocation(data);
    }

    /// <summary>
    ///     Parse Misc Stats from Global Data Type 0.
    ///     Delegates to <see cref="ChangedFormParser"/>.
    /// </summary>
    private static SaveStatistics ParseMiscStats(ReadOnlySpan<byte> data)
    {
        return ChangedFormParser.ParseMiscStats(data);
    }

    /// <summary>
    ///     Parse global variables from Global Data Type 3.
    ///     Delegates to <see cref="ChangedFormParser"/>.
    /// </summary>
    private static List<GlobalVariable> ParseGlobalVariables(ReadOnlySpan<byte> data)
    {
        return ChangedFormParser.ParseGlobalVariables(data);
    }

    #region Pipe-terminated field readers

    /// <summary>Read a uint32 followed by a pipe terminator (0x7C).</summary>
    private static uint ReadUInt32T(ReadOnlySpan<byte> data, ref int position)
    {
        var value = BinaryUtils.ReadUInt32LE(data, position);
        position += 4;
        if (position < data.Length && data[position] == PipeTerminator)
        {
            position++;
        }

        return value;
    }

    /// <summary>Read a uint16-length-prefixed string followed by a pipe terminator.</summary>
    private static string ReadLenStringT(ReadOnlySpan<byte> data, ref int position)
    {
        var length = BinaryUtils.ReadUInt16LE(data, position);
        position += 2;
        if (position < data.Length && data[position] == PipeTerminator)
        {
            position++;
        }

        if (length == 0)
        {
            return "";
        }

        var value = Encoding.ASCII.GetString(data.Slice(position, length));
        position += length;
        if (position < data.Length && data[position] == PipeTerminator)
        {
            position++;
        }

        return value;
    }

    #endregion
}
