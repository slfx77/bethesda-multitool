using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Parser for Fallout 3/NV save files (FO3SAVEGAME format).
///     Handles both raw .fos files and .fxs files wrapped in Xbox 360 STFS containers.
/// </summary>
public static class SaveFileParser
{
    private static readonly byte[] Magic = "FO3SAVEGAME"u8.ToArray();
    private const byte PipeTerminator = 0x7C;

    /// <summary>
    ///     Parse a save file from raw bytes (may be STFS-wrapped or raw FO3SAVEGAME).
    /// </summary>
    public static SaveFile Parse(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> payload;
        int stfsPayloadOffset = 0;
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

        var header = ParseHeader(payload, out int headerEnd, exactPayloadSize);
        var locationTable = ParseFileLocationTable(payload, ref headerEnd);

        // FLT offsets are absolute from save payload start
        int bodyBase = 0;

        var globalData1 = ParseGlobalDataEntries(payload, bodyBase, locationTable.GlobalDataTable1Offset, locationTable.GlobalDataTable1Count);
        var changedForms = ParseChangedForms(payload, bodyBase, locationTable.ChangedFormsOffset, locationTable.ChangedFormsCount);
        var globalData2 = ParseGlobalDataEntries(payload, bodyBase, locationTable.GlobalDataTable2Offset, locationTable.GlobalDataTable2Count);

        // Parse FormID array
        int formIdArrayOffset = bodyBase + (int)locationTable.RefIdArrayCountOffset;
        var formIdArray = ParseFormIdArray(payload, formIdArrayOffset, out int afterFormIds);

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
        for (int offset = 0x1000; offset < data.Length - Magic.Length; offset += 0x1000)
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
    public static int FindPayloadOffset(ReadOnlySpan<byte> data) => FindMagicOffset(data);

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
        uint headerSize = BinaryUtils.ReadUInt32LE(data, position);
        position += 4;

        int headerStart = position;

        // Parse pipe-terminated fields within the header
        uint version = ReadUInt32T(data, ref position);
        uint screenshotWidth = ReadUInt32T(data, ref position);
        uint screenshotHeight = ReadUInt32T(data, ref position);
        uint saveNumber = ReadUInt32T(data, ref position);
        string playerName = ReadLenStringT(data, ref position);
        string playerStatus = ReadLenStringT(data, ref position);
        uint playerLevel = ReadUInt32T(data, ref position);
        string playerCell = ReadLenStringT(data, ref position);
        string saveDuration = ReadLenStringT(data, ref position);

        // Skip to end of header
        int headerEnd = headerStart + (int)headerSize;
        if (position < headerEnd)
        {
            position = headerEnd;
        }

        // Screenshot data - try standard 3bpp first, then search for formVersion marker
        int screenshotDataOffset = position;
        int screenshotDataSize = (int)(screenshotWidth * screenshotHeight * 3);
        byte formVersion = 0;
        uint pluginInfoSize = 0;

        // Bound the search to the actual payload size when known
        int maxSearchEnd = payloadSize.HasValue
            ? Math.Min(payloadSize.Value, data.Length)
            : data.Length;

        // Try 3bpp and 4bpp screenshot sizes
        bool found = false;
        foreach (int bpp in new[] { 3, 4 })
        {
            int trySize = (int)(screenshotWidth * screenshotHeight * bpp);
            int fvPos = headerEnd + trySize;
            if (fvPos + 5 < maxSearchEnd)
            {
                byte fv = data[fvPos];
                uint piSize = BinaryUtils.ReadUInt32LE(data, fvPos + 1);
                if (fv is >= 19 and <= 22 && piSize < 1000)
                {
                    // Validate: FLT after plugins should produce sane offsets
                    if (ValidateFormVersionCandidate(data, fvPos + 5 + (int)piSize, maxSearchEnd))
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
        }

        // Fallback: search for formVersion marker validated by plugin structure (.esm/.esp) AND FLT
        if (!found)
        {
            int searchEnd = Math.Min(headerEnd + (int)(screenshotWidth * screenshotHeight * 8), maxSearchEnd - 5);
            for (int i = headerEnd; i < searchEnd; i++)
            {
                byte fvCandidate = data[i];
                if (fvCandidate is < 19 or > 22)
                {
                    continue;
                }

                uint piSize = BinaryUtils.ReadUInt32LE(data, i + 1);
                if (piSize is < 1 or > 1000)
                {
                    continue;
                }

                // Validate: plugin info section should contain .esm or .esp filename
                int pluginRegionStart = i + 5;
                int pluginRegionEnd = Math.Min(pluginRegionStart + (int)piSize, maxSearchEnd);
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
                int fltPosition = pluginRegionStart + (int)piSize;
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
                byte fvCandidate = data[position];
                uint piCandidate = BinaryUtils.ReadUInt32LE(data, position + 1);
                int fltPos = position + 5 + (int)piCandidate;

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
        int pluginsEnd = position + (int)pluginInfoSize;
        if (position < data.Length)
        {
            byte pluginCount = data[position];
            position++;

            // Skip pipe separator after plugin count
            if (position < data.Length && data[position] == PipeTerminator)
            {
                position++;
            }

            for (int i = 0; i < pluginCount && position < pluginsEnd; i++)
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
        uint refIdOffset = BinaryUtils.ReadUInt32LE(data, fltPosition);
        uint gdt1Offset = BinaryUtils.ReadUInt32LE(data, fltPosition + 8);
        uint cfOffset = BinaryUtils.ReadUInt32LE(data, fltPosition + 12);
        uint gdt1Count = BinaryUtils.ReadUInt32LE(data, fltPosition + 20);
        uint cfCount = BinaryUtils.ReadUInt32LE(data, fltPosition + 28);

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
    /// </summary>
    private static List<GlobalDataEntry> ParseGlobalDataEntries(ReadOnlySpan<byte> data, int bodyBase, uint sectionOffset, uint count)
    {
        var entries = new List<GlobalDataEntry>();
        int pos = bodyBase + (int)sectionOffset;

        // Guard against garbage offsets from misdetected header
        if (pos < 0 || pos >= data.Length)
        {
            return entries;
        }

        for (uint i = 0; i < count && pos + 8 <= data.Length; i++)
        {
            uint type = BinaryUtils.ReadUInt32LE(data, pos);
            uint length = BinaryUtils.ReadUInt32LE(data, pos + 4);
            pos += 8;

            int intLength = (int)length;
            if (intLength < 0 || (long)pos + intLength > data.Length)
            {
                break;
            }

            entries.Add(new GlobalDataEntry
            {
                Type = type,
                Data = data.Slice(pos, intLength).ToArray()
            });

            pos += intLength;
        }

        return entries;
    }

    /// <summary>
    ///     Parse Changed Form entries from the body.
    /// </summary>
    private static List<ChangedForm> ParseChangedForms(ReadOnlySpan<byte> data, int bodyBase, uint sectionOffset, uint count)
    {
        var forms = new List<ChangedForm>();
        int pos = bodyBase + (int)sectionOffset;

        // Guard against garbage offsets from misdetected header
        if (pos < 0 || pos >= data.Length)
        {
            return forms;
        }

        for (uint i = 0; i < count && pos + 14 <= data.Length; i++)
        {
            // RefID: 3 bytes
            var refId = SaveRefId.Read(data, pos);
            pos += 3;

            // ChangeFlags: uint32
            uint changeFlags = BinaryUtils.ReadUInt32LE(data, pos);
            pos += 4;

            // Type byte: bits 0-5 = type, bits 6-7 = length code
            byte rawType = data[pos];
            pos++;

            byte changeType = (byte)(rawType & 0x3F);
            int lengthCode = rawType >> 6;

            // Version: uint8
            byte version = data[pos];
            pos++;

            // Data length (depends on length code)
            int dataLength = lengthCode switch
            {
                0 => data[pos++],
                1 => ReadUInt16Advance(data, ref pos),
                2 => (int)ReadUInt32Advance(data, ref pos),
                _ => 0
            };

            if (dataLength < 0 || (long)pos + dataLength > data.Length)
            {
                break;
            }

            byte[] formData = data.Slice(pos, dataLength).ToArray();

            // Parse initial data for reference types
            InitialData? initialData = null;
            if (IsReferenceType(changeType) && formData.Length > 0)
            {
                initialData = TryParseInitialData(formData, changeType, changeFlags, refId);
            }

            forms.Add(new ChangedForm
            {
                RefId = refId,
                ChangeFlags = changeFlags,
                ChangeType = changeType,
                Version = version,
                Data = formData,
                Initial = initialData
            });

            pos += dataLength;
        }

        return forms;
    }

    /// <summary>
    ///     Try to parse initial data (position/cell) from a reference-type changed form.
    /// </summary>
#pragma warning disable S1172 // changeType reserved for future per-type parsing
    private static InitialData? TryParseInitialData(ReadOnlySpan<byte> formData, byte _changeType, uint changeFlags, SaveRefId refId)
#pragma warning restore S1172
    {
        try
        {
            int pos = 0;
            bool isCreated = refId.Type == SaveRefIdType.Created;
            bool hasMoved = (changeFlags & 0x02) != 0; // CHANGE_REFR_MOVE
            bool hasCellChanged = (changeFlags & 0x08) != 0; // CHANGE_REFR_CELL_CHANGED

            if (isCreated)
            {
                // Type 5: Created form
                if (formData.Length < 30)
                {
                    return null;
                }

                var cellRef = SaveRefId.Read(formData, pos);
                pos += 3;
                float posX = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float posY = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float posZ = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float rotX = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float rotY = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float rotZ = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                pos++; // flags uint8
                var baseFormRef = SaveRefId.Read(formData, pos);

                return new InitialData
                {
                    DataType = 5,
                    CellRefId = cellRef,
                    PosX = posX, PosY = posY, PosZ = posZ,
                    RotX = rotX, RotY = rotY, RotZ = rotZ,
                    BaseFormRefId = baseFormRef
                };
            }

            if (hasCellChanged)
            {
                // Type 6: Cell changed
                if (formData.Length < 31)
                {
                    return null;
                }

                var cellRef = SaveRefId.Read(formData, pos);
                pos += 3;
                float posX = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float posY = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float posZ = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float rotX = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float rotY = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float rotZ = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                var newCellRef = SaveRefId.Read(formData, pos);
                pos += 3;
                short newCoordX = BinaryPrimitives.ReadInt16LittleEndian(formData[pos..]);
                pos += 2;
                short newCoordY = BinaryPrimitives.ReadInt16LittleEndian(formData[pos..]);

                return new InitialData
                {
                    DataType = 6,
                    CellRefId = cellRef,
                    PosX = posX, PosY = posY, PosZ = posZ,
                    RotX = rotX, RotY = rotY, RotZ = rotZ,
                    NewCellRefId = newCellRef,
                    NewCoordX = newCoordX,
                    NewCoordY = newCoordY
                };
            }

            if (hasMoved)
            {
                // Type 4: Moved
                if (formData.Length < 27)
                {
                    return null;
                }

                var cellRef = SaveRefId.Read(formData, pos);
                pos += 3;
                float posX = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float posY = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float posZ = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float rotX = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float rotY = BinaryUtils.ReadFloatLE(formData, pos); pos += 4;
                float rotZ = BinaryUtils.ReadFloatLE(formData, pos);

                return new InitialData
                {
                    DataType = 4,
                    CellRefId = cellRef,
                    PosX = posX, PosY = posY, PosZ = posZ,
                    RotX = rotX, RotY = rotY, RotZ = rotZ
                };
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Parse the FormID lookup array.
    /// </summary>
    private static List<uint> ParseFormIdArray(ReadOnlySpan<byte> data, int offset, out int endOffset)
    {
        var result = new List<uint>();
        endOffset = offset;

        if (offset < 0 || offset + 4 > data.Length)
        {
            return result;
        }

        uint count = BinaryUtils.ReadUInt32LE(data, offset);
        endOffset = offset + 4;

        // Sanity cap to prevent allocating huge lists from garbage data
        if (count > 1_000_000)
        {
            return result;
        }

        for (uint i = 0; i < count && endOffset + 4 <= data.Length; i++)
        {
            result.Add(BinaryUtils.ReadUInt32LE(data, endOffset));
            endOffset += 4;
        }

        return result;
    }

    /// <summary>
    ///     Parse player location from Global Data Type 1 (TES).
    /// </summary>
    private static PlayerLocation? ParsePlayerLocation(ReadOnlySpan<byte> data)
    {
        try
        {
            int pos = 0;

            // Worldspace RefID
            var worldspaceRefId = SaveRefId.Read(data, pos);
            pos += 3;

            // Coord X/Y (int32)
            int coordX = BinaryUtils.ReadInt32LE(data, pos);
            pos += 4;
            int coordY = BinaryUtils.ReadInt32LE(data, pos);
            pos += 4;

            // Cell RefID
            var cellRefId = SaveRefId.Read(data, pos);
            pos += 3;

            // Player position
            float posX = BinaryUtils.ReadFloatLE(data, pos); pos += 4;
            float posY = BinaryUtils.ReadFloatLE(data, pos); pos += 4;
            float posZ = BinaryUtils.ReadFloatLE(data, pos);

            return new PlayerLocation
            {
                WorldspaceRefId = worldspaceRefId,
                CoordX = coordX,
                CoordY = coordY,
                CellRefId = cellRefId,
                PosX = posX,
                PosY = posY,
                PosZ = posZ
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Parse Misc Stats from Global Data Type 0.
    ///     Format: uint32 count + pipe + (uint32 value + pipe) × count.
    /// </summary>
    private static SaveStatistics ParseMiscStats(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
        {
            return new SaveStatistics();
        }

        int pos = 0;
        uint count = ReadUInt32T(data, ref pos);

        if (count > 100)
        {
            return new SaveStatistics();
        }

        var values = new List<uint>((int)count);
        for (uint i = 0; i < count && pos + 4 <= data.Length; i++)
        {
            values.Add(ReadUInt32T(data, ref pos));
        }

        return new SaveStatistics { Values = values };
    }

    /// <summary>
    ///     Parse global variables from Global Data Type 3.
    /// </summary>
    private static List<GlobalVariable> ParseGlobalVariables(ReadOnlySpan<byte> data)
    {
        var result = new List<GlobalVariable>();
        int pos = 0;

        while (pos + 7 <= data.Length)
        {
            var refId = SaveRefId.Read(data, pos);
            pos += 3;
            float value = BinaryUtils.ReadFloatLE(data, pos);
            pos += 4;
            result.Add(new GlobalVariable(refId, value));
        }

        return result;
    }

    private static bool IsReferenceType(byte changeType)
    {
        return changeType is 0 or 1 or 2 or 3 or 4 or 5 or 6 or 44;
    }

    #region Pipe-terminated field readers

    /// <summary>Read a uint32 followed by a pipe terminator (0x7C).</summary>
    private static uint ReadUInt32T(ReadOnlySpan<byte> data, ref int position)
    {
        uint value = BinaryUtils.ReadUInt32LE(data, position);
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
        ushort length = BinaryUtils.ReadUInt16LE(data, position);
        position += 2;
        if (position < data.Length && data[position] == PipeTerminator)
        {
            position++;
        }

        if (length == 0)
        {
            return "";
        }

        string value = Encoding.ASCII.GetString(data.Slice(position, length));
        position += length;
        if (position < data.Length && data[position] == PipeTerminator)
        {
            position++;
        }

        return value;
    }

    private static ushort ReadUInt16Advance(ReadOnlySpan<byte> data, ref int position)
    {
        ushort value = BinaryUtils.ReadUInt16LE(data, position);
        position += 2;
        return value;
    }

    private static uint ReadUInt32Advance(ReadOnlySpan<byte> data, ref int position)
    {
        uint value = BinaryUtils.ReadUInt32LE(data, position);
        position += 4;
        return value;
    }

    #endregion
}
