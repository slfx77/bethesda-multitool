using System.Text;

namespace EsmAnalyzer.Core;

/// <summary>
///     Shared helper for decoding subrecord field values from binary data.
/// </summary>
public static class FieldValueDecoder
{
    /// <summary>
    ///     Decodes a field value from binary data based on its type.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> data, SubrecordFieldType type, bool bigEndian)
    {
        try
        {
            return type switch
            {
                SubrecordFieldType.UInt8 => data[0].ToString(),
                SubrecordFieldType.Int8 => ((sbyte)data[0]).ToString(),
                SubrecordFieldType.UInt16 => BinaryUtils.ReadUInt16(data, 0, bigEndian).ToString(),
                SubrecordFieldType.Int16 => BinaryUtils.ReadInt16(data, 0, bigEndian).ToString(),
                SubrecordFieldType.UInt16LittleEndian => BinaryUtils.ReadUInt16(data, 0, false).ToString(),
                SubrecordFieldType.UInt32 => BinaryUtils.ReadUInt32(data, 0, bigEndian).ToString(),
                SubrecordFieldType.Int32 => BinaryUtils.ReadInt32(data, 0, bigEndian).ToString(),
                SubrecordFieldType.UInt32WordSwapped => DecodeWordSwapped(data, bigEndian).ToString(),
                SubrecordFieldType.Float => FormatFloat(BinaryUtils.ReadFloat(data, 0, bigEndian)),
                SubrecordFieldType.FormId => $"0x{BinaryUtils.ReadUInt32(data, 0, bigEndian):X8}",
                SubrecordFieldType.FormIdLittleEndian => $"0x{BinaryUtils.ReadUInt32(data, 0, false):X8}",
                SubrecordFieldType.UInt64 => BinaryUtils.ReadUInt64(data, 0, bigEndian).ToString(),
                SubrecordFieldType.Int64 => BinaryUtils.ReadInt64(data, 0, bigEndian).ToString(),
                SubrecordFieldType.Double => FormatDouble(BinaryUtils.ReadDouble(data, 0, bigEndian)),
                SubrecordFieldType.Vec3 => FormatVec3(data, bigEndian),
                SubrecordFieldType.Quaternion => FormatQuaternion(data, bigEndian),
                SubrecordFieldType.PosRot => FormatPosRot(data, bigEndian),
                SubrecordFieldType.ColorRgba => FormatColorRgba(data),
                SubrecordFieldType.ColorArgb => FormatColorArgb(data),
                SubrecordFieldType.String => Encoding.ASCII.GetString(data).TrimEnd('\0'),
                SubrecordFieldType.ByteArray => FormatBytes(data),
                SubrecordFieldType.Padding => $"Pad:{FormatBytes(data)}",
                _ => FormatBytes(data)
            };
        }
        catch
        {
            return FormatBytes(data);
        }
    }

    /// <summary>
    ///     Decodes a word-swapped uint32 (Xbox 360 specific format).
    /// </summary>
    public static uint DecodeWordSwapped(ReadOnlySpan<byte> data, bool bigEndian)
    {
        // Word-swapped on Xbox: bytes [0,1,2,3] represent uint32 with words swapped
        // Xbox stores as [lo-hi of high word][lo-hi of low word] in big-endian
        if (bigEndian)
        {
            // Xbox: interpret as word-swapped big-endian
            var highWord = (uint)(data[0] << 8 | data[1]);
            var lowWord = (uint)(data[2] << 8 | data[3]);
            return (lowWord << 16) | highWord;
        }
        else
        {
            // PC: normal little-endian
            return (uint)(data[0] | data[1] << 8 | data[2] << 16 | data[3] << 24);
        }
    }

    /// <summary>
    ///     Formats a float value for display.
    /// </summary>
    public static string FormatFloat(float f)
    {
        if (float.IsNaN(f))
        {
            return "NaN";
        }

        if (float.IsInfinity(f))
        {
            return f > 0 ? "+Inf" : "-Inf";
        }

        return f.ToString("G6");
    }

    /// <summary>
    ///     Formats a double value for display.
    /// </summary>
    public static string FormatDouble(double d)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }

        if (double.IsInfinity(d))
        {
            return d > 0 ? "+Inf" : "-Inf";
        }

        return d.ToString("G8");
    }

    /// <summary>
    ///     Formats a Vec3 (3 floats).
    /// </summary>
    public static string FormatVec3(ReadOnlySpan<byte> data, bool bigEndian)
    {
        var x = BinaryUtils.ReadFloat(data, 0, bigEndian);
        var y = BinaryUtils.ReadFloat(data, 4, bigEndian);
        var z = BinaryUtils.ReadFloat(data, 8, bigEndian);
        return $"({x:F2}, {y:F2}, {z:F2})";
    }

    /// <summary>
    ///     Formats a Quaternion (4 floats: w, x, y, z).
    /// </summary>
    public static string FormatQuaternion(ReadOnlySpan<byte> data, bool bigEndian)
    {
        var w = BinaryUtils.ReadFloat(data, 0, bigEndian);
        var x = BinaryUtils.ReadFloat(data, 4, bigEndian);
        var y = BinaryUtils.ReadFloat(data, 8, bigEndian);
        var z = BinaryUtils.ReadFloat(data, 12, bigEndian);
        return $"({w:F3}, {x:F3}, {y:F3}, {z:F3})";
    }

    /// <summary>
    ///     Formats a PosRot (position + rotation, 6 floats).
    /// </summary>
    public static string FormatPosRot(ReadOnlySpan<byte> data, bool bigEndian)
    {
        var px = BinaryUtils.ReadFloat(data, 0, bigEndian);
        var py = BinaryUtils.ReadFloat(data, 4, bigEndian);
        var pz = BinaryUtils.ReadFloat(data, 8, bigEndian);
        var rx = BinaryUtils.ReadFloat(data, 12, bigEndian);
        var ry = BinaryUtils.ReadFloat(data, 16, bigEndian);
        var rz = BinaryUtils.ReadFloat(data, 20, bigEndian);
        return $"Pos({px:F1},{py:F1},{pz:F1}) Rot({rx:F2},{ry:F2},{rz:F2})";
    }

    /// <summary>
    ///     Formats an RGBA color.
    /// </summary>
    public static string FormatColorRgba(ReadOnlySpan<byte> data)
    {
        return $"RGBA({data[0]},{data[1]},{data[2]},{data[3]})";
    }

    /// <summary>
    ///     Formats an ARGB color.
    /// </summary>
    public static string FormatColorArgb(ReadOnlySpan<byte> data)
    {
        return $"ARGB({data[0]},{data[1]},{data[2]},{data[3]})";
    }

    /// <summary>
    ///     Formats raw bytes for display.
    /// </summary>
    public static string FormatBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 8)
        {
            return string.Join(" ", data.ToArray().Select(b => $"{b:X2}"));
        }

        return $"{data.Length} bytes";
    }

    /// <summary>
    ///     Tries to parse a FormID from a hex string.
    /// </summary>
    public static bool TryParseFormId(string? value, out uint formId)
    {
        formId = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out formId);
        }

        return uint.TryParse(value, out formId);
    }
}
