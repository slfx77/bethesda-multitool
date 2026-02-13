using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

/// <summary>
///     Reads subrecord data using schema definitions.
///     Unlike SubrecordSchemaProcessor (which converts BE→LE), this reads values
///     respecting the source endianness for extraction/analysis.
/// </summary>
public static class SubrecordSchemaReader
{
    /// <summary>
    ///     Reads all fields from a subrecord using its schema.
    ///     Returns a dictionary of field name → value.
    /// </summary>
    public static Dictionary<string, object?> ReadFields(
        string signature,
        ReadOnlySpan<byte> data,
        string recordType,
        bool bigEndian)
    {
        var result = new Dictionary<string, object?>();
        var schema = SubrecordSchemaRegistry.GetSchema(signature, recordType, data.Length);

        if (schema == null)
        {
            return result;
        }

        var offset = 0;
        foreach (var field in schema.Fields)
        {
            if (offset >= data.Length)
            {
                break;
            }

            var size = field.EffectiveSize;
            if (offset + size > data.Length)
            {
                break;
            }

            var value = ReadField(data.Slice(offset, size), field.Type, bigEndian);
            if (value != null && !string.IsNullOrEmpty(field.Name))
            {
                result[field.Name] = value;
            }

            offset += size;
        }

        return result;
    }

    /// <summary>
    ///     Reads a single typed value from a subrecord field.
    /// </summary>
    public static object? ReadField(ReadOnlySpan<byte> data, SubrecordFieldType type, bool bigEndian)
    {
        return type switch
        {
            SubrecordFieldType.UInt8 => data.Length >= 1 ? data[0] : null,
            SubrecordFieldType.Int8 => data.Length >= 1 ? (sbyte)data[0] : null,
            SubrecordFieldType.UInt16 => data.Length >= 2 ? BinaryUtils.ReadUInt16(data, 0, bigEndian) : null,
            SubrecordFieldType.Int16 => data.Length >= 2 ? BinaryUtils.ReadInt16(data, 0, bigEndian) : null,
            SubrecordFieldType.UInt16LittleEndian => data.Length >= 2 ? BinaryUtils.ReadUInt16LE(data) : null,
            SubrecordFieldType.UInt32 => data.Length >= 4 ? BinaryUtils.ReadUInt32(data, 0, bigEndian) : null,
            SubrecordFieldType.Int32 => data.Length >= 4 ? BinaryUtils.ReadInt32(data, 0, bigEndian) : null,
            SubrecordFieldType.UInt32WordSwapped => ReadUInt32WordSwapped(data),
            SubrecordFieldType.Float => data.Length >= 4 ? BinaryUtils.ReadFloat(data, 0, bigEndian) : null,
            SubrecordFieldType.FormId => data.Length >= 4 ? BinaryUtils.ReadUInt32(data, 0, bigEndian) : null,
            SubrecordFieldType.FormIdLittleEndian => data.Length >= 4 ? BinaryUtils.ReadUInt32LE(data) : null,
            SubrecordFieldType.UInt64 => data.Length >= 8 ? BinaryUtils.ReadUInt64(data, 0, bigEndian) : null,
            SubrecordFieldType.Int64 => data.Length >= 8 ? BinaryUtils.ReadInt64(data, 0, bigEndian) : null,
            SubrecordFieldType.Double => data.Length >= 8 ? BinaryUtils.ReadDouble(data, 0, bigEndian) : null,
            SubrecordFieldType.Vec3 => ReadVec3(data, bigEndian),
            SubrecordFieldType.Quaternion => ReadQuaternion(data, bigEndian),
            SubrecordFieldType.PosRot => ReadPosRot(data, bigEndian),
            SubrecordFieldType.ColorRgba => ReadColorRgba(data),
            SubrecordFieldType.ColorArgb => ReadColorArgb(data, bigEndian),
            SubrecordFieldType.String => ReadString(data),
            SubrecordFieldType.ByteArray => data.ToArray(),
            SubrecordFieldType.Padding => null,
            _ => null
        };
    }

    /// <summary>
    ///     Reads a float value from VHGT heightmap data.
    ///     VHGT structure: HeightOffset (4-byte float) + HeightDeltas (1089 sbytes) + Padding (3 bytes)
    /// </summary>
    public static (float heightOffset, sbyte[] deltas)? ReadVhgtHeightmap(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length < 1093) // 4 + 1089
        {
            return null;
        }

        var heightOffset = BinaryUtils.ReadFloat(data, 0, bigEndian);

        var deltas = new sbyte[1089];
        for (var i = 0; i < 1089 && i + 4 < data.Length; i++)
        {
            deltas[i] = (sbyte)data[4 + i];
        }

        return (heightOffset, deltas);
    }

    /// <summary>
    ///     Reads XCLC cell grid coordinates.
    ///     XCLC structure: GridX (int32) + GridY (int32) + Flags (uint32)
    /// </summary>
    public static (int gridX, int gridY, uint flags)? ReadXclcCellGrid(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length < 8)
        {
            return null;
        }

        var gridX = BinaryUtils.ReadInt32(data, 0, bigEndian);
        var gridY = BinaryUtils.ReadInt32(data, 4, bigEndian);
        var flags = data.Length >= 12 ? BinaryUtils.ReadUInt32(data, 8, bigEndian) : 0u;

        return (gridX, gridY, flags);
    }

    /// <summary>
    ///     Reads DATA position/rotation for REFR records.
    ///     DATA structure: PosX, PosY, PosZ, RotX, RotY, RotZ (6 floats = 24 bytes)
    /// </summary>
    public static (float x, float y, float z, float rotX, float rotY, float rotZ)? ReadDataPosition(
        ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length < 24)
        {
            return null;
        }

        return (
            BinaryUtils.ReadFloat(data, 0, bigEndian),
            BinaryUtils.ReadFloat(data, 4, bigEndian),
            BinaryUtils.ReadFloat(data, 8, bigEndian),
            BinaryUtils.ReadFloat(data, 12, bigEndian),
            BinaryUtils.ReadFloat(data, 16, bigEndian),
            BinaryUtils.ReadFloat(data, 20, bigEndian)
        );
    }

    /// <summary>
    ///     Reads NAME base object FormID for REFR records.
    /// </summary>
    public static uint? ReadNameFormId(ReadOnlySpan<byte> data, bool bigEndian)
    {
        return data.Length >= 4 ? BinaryUtils.ReadUInt32(data, 0, bigEndian) : null;
    }

    /// <summary>
    ///     Reads XSCL scale factor for REFR records.
    /// </summary>
    public static float? ReadXsclScale(ReadOnlySpan<byte> data, bool bigEndian)
    {
        return data.Length >= 4 ? BinaryUtils.ReadFloat(data, 0, bigEndian) : null;
    }

    #region Private Readers

    private static uint? ReadUInt32WordSwapped(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        // Xbox stores as two BE uint16 words in LE order
        var highWord = BinaryPrimitives.ReadUInt16BigEndian(data);
        var lowWord = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        return ((uint)highWord << 16) | lowWord;
    }

    private static (float x, float y, float z)? ReadVec3(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length < 12)
        {
            return null;
        }

        return (
            BinaryUtils.ReadFloat(data, 0, bigEndian),
            BinaryUtils.ReadFloat(data, 4, bigEndian),
            BinaryUtils.ReadFloat(data, 8, bigEndian)
        );
    }

    private static (float w, float x, float y, float z)? ReadQuaternion(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length < 16)
        {
            return null;
        }

        return (
            BinaryUtils.ReadFloat(data, 0, bigEndian),
            BinaryUtils.ReadFloat(data, 4, bigEndian),
            BinaryUtils.ReadFloat(data, 8, bigEndian),
            BinaryUtils.ReadFloat(data, 12, bigEndian)
        );
    }

    private static (float x, float y, float z, float rotX, float rotY, float rotZ)? ReadPosRot(
        ReadOnlySpan<byte> data, bool bigEndian)
    {
        return ReadDataPosition(data, bigEndian);
    }

    private static (byte r, byte g, byte b, byte a)? ReadColorRgba(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        return (data[0], data[1], data[2], data[3]);
    }

    private static (byte r, byte g, byte b, byte a)? ReadColorArgb(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length < 4)
        {
            return null;
        }

        // Xbox ARGB format
        return bigEndian
            ? (data[1], data[2], data[3], data[0]) // ARGB → RGBA
            : (data[0], data[1], data[2], data[3]); // Already RGBA on PC
    }

    private static string? ReadString(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return null;
        }

        // Find null terminator
        var nullIndex = data.IndexOf((byte)0);
        var length = nullIndex >= 0 ? nullIndex : data.Length;
        return Encoding.Latin1.GetString(data[..length]);
    }

    #endregion
}
