using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Parses save file body sections: changed forms, global data entries, FormID arrays,
///     and global data type decoders (player location, misc stats, global variables).
///     Extracted from <see cref="SaveFileParser"/> for maintainability.
/// </summary>
internal static class ChangedFormParser
{
    private const byte PipeTerminator = 0x7C;

    /// <summary>
    ///     Parse Global Data entries from a section.
    /// </summary>
    internal static List<GlobalDataEntry> ParseGlobalDataEntries(ReadOnlySpan<byte> data, int bodyBase,
        uint sectionOffset, uint count)
    {
        var entries = new List<GlobalDataEntry>();
        var pos = bodyBase + (int)sectionOffset;

        // Guard against garbage offsets from misdetected header
        if (pos < 0 || pos >= data.Length)
        {
            return entries;
        }

        for (uint i = 0; i < count && pos + 8 <= data.Length; i++)
        {
            var type = BinaryUtils.ReadUInt32LE(data, pos);
            var length = BinaryUtils.ReadUInt32LE(data, pos + 4);
            pos += 8;

            var intLength = (int)length;
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
    internal static List<ChangedForm> ParseChangedForms(ReadOnlySpan<byte> data, int bodyBase, uint sectionOffset,
        uint count)
    {
        var forms = new List<ChangedForm>();
        var pos = bodyBase + (int)sectionOffset;

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
            var changeFlags = BinaryUtils.ReadUInt32LE(data, pos);
            pos += 4;

            // Type byte: bits 0-5 = type, bits 6-7 = length code
            var rawType = data[pos];
            pos++;

            var changeType = (byte)(rawType & 0x3F);
            var lengthCode = rawType >> 6;

            // Version: uint8
            var version = data[pos];
            pos++;

            // Data length (depends on length code)
            var dataLength = lengthCode switch
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

            var formData = data.Slice(pos, dataLength).ToArray();

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
    private static InitialData? TryParseInitialData(ReadOnlySpan<byte> formData, byte _changeType, uint changeFlags,
        SaveRefId refId)
#pragma warning restore S1172
    {
        try
        {
            var pos = 0;
            var isCreated = refId.Type == SaveRefIdType.Created;
            var hasMoved = (changeFlags & 0x02) != 0; // CHANGE_REFR_MOVE
            var hasCellChanged = (changeFlags & 0x08) != 0; // CHANGE_REFR_CELL_CHANGED

            if (isCreated)
            {
                // Type 5: Created form
                if (formData.Length < 30)
                {
                    return null;
                }

                var cellRef = SaveRefId.Read(formData, pos);
                pos += 3;
                var posX = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var posY = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var posZ = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var rotX = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var rotY = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var rotZ = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
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
                var posX = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var posY = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var posZ = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var rotX = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var rotY = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var rotZ = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var newCellRef = SaveRefId.Read(formData, pos);
                pos += 3;
                var newCoordX = BinaryPrimitives.ReadInt16LittleEndian(formData[pos..]);
                pos += 2;
                var newCoordY = BinaryPrimitives.ReadInt16LittleEndian(formData[pos..]);

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
                var posX = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var posY = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var posZ = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var rotX = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var rotY = BinaryUtils.ReadFloatLE(formData, pos);
                pos += 4;
                var rotZ = BinaryUtils.ReadFloatLE(formData, pos);

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

    private static bool IsReferenceType(byte changeType)
    {
        return changeType is 0 or 1 or 2 or 3 or 4 or 5 or 6 or 44;
    }

    private static ushort ReadUInt16Advance(ReadOnlySpan<byte> data, ref int position)
    {
        var value = BinaryUtils.ReadUInt16LE(data, position);
        position += 2;
        return value;
    }

    private static uint ReadUInt32Advance(ReadOnlySpan<byte> data, ref int position)
    {
        var value = BinaryUtils.ReadUInt32LE(data, position);
        position += 4;
        return value;
    }

    /// <summary>
    ///     Parse the FormID lookup array.
    /// </summary>
    internal static List<uint> ParseFormIdArray(ReadOnlySpan<byte> data, int offset, out int endOffset)
    {
        var result = new List<uint>();
        endOffset = offset;

        if (offset < 0 || offset + 4 > data.Length)
        {
            return result;
        }

        var count = BinaryUtils.ReadUInt32LE(data, offset);
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
    ///     Fields are pipe-terminated (0x7C) like changed form data.
    /// </summary>
    internal static PlayerLocation? ParsePlayerLocation(ReadOnlySpan<byte> data)
    {
        try
        {
            var r = new FormDataReader(data, []);

            // Worldspace RefID (3B) + unknown byte (flags/version) + pipe
            var worldspaceRefId = r.ReadRefId();
            if (r.HasData(1)) r.ReadByte();
            r.TrySkipPipe();

            // Second RefID (persistent cell or parent) + pipe
            if (r.HasData(3))
            {
                r.ReadRefId();
                r.TrySkipPipe();
            }

            // Coord X/Y (int32 each, pipe-terminated)
            var coordX = r.HasData(4) ? r.ReadInt32() : 0;
            r.TrySkipPipe();
            var coordY = r.HasData(4) ? r.ReadInt32() : 0;
            r.TrySkipPipe();

            // Cell RefID (3B) + pipe
            var cellRefId = r.HasData(3) ? r.ReadRefId() : default;
            r.TrySkipPipe();

            // Player position (3 floats, pipe after group)
            var posX = r.HasData(4) ? r.ReadFloat() : 0;
            var posY = r.HasData(4) ? r.ReadFloat() : 0;
            var posZ = r.HasData(4) ? r.ReadFloat() : 0;

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
    ///     Format: uint32 count + pipe + (uint32 value + pipe) x count.
    /// </summary>
    internal static SaveStatistics ParseMiscStats(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
        {
            return new SaveStatistics();
        }

        var pos = 0;
        var count = ReadUInt32T(data, ref pos);

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
    ///     Format: [vsval count + pipe] ([RefID + pipe] [float + pipe]) x N
    /// </summary>
    internal static List<GlobalVariable> ParseGlobalVariables(ReadOnlySpan<byte> data)
    {
        var result = new List<GlobalVariable>();
        var r = new FormDataReader(data, []);

        var count = r.ReadVsval();
        r.TrySkipPipe();

        for (uint i = 0; i < count && r.HasData(3); i++)
        {
            var refId = r.ReadRefId();
            r.TrySkipPipe();
            var value = r.HasData(4) ? r.ReadFloat() : 0;
            r.TrySkipPipe();
            result.Add(new GlobalVariable(refId, value));
        }

        return result;
    }

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
}
