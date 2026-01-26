using System.Text;
using EsmAnalyzer.Conversion.Schema;

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
                SubrecordFieldType.UInt16 => EsmBinary.ReadUInt16(data, bigEndian).ToString(),
                SubrecordFieldType.Int16 => EsmBinary.ReadInt16(data, bigEndian).ToString(),
                SubrecordFieldType.UInt16LittleEndian => EsmBinary.ReadUInt16(data, false).ToString(),
                SubrecordFieldType.UInt32 => EsmBinary.ReadUInt32(data, bigEndian).ToString(),
                SubrecordFieldType.Int32 => EsmBinary.ReadInt32(data, bigEndian).ToString(),
                SubrecordFieldType.UInt32WordSwapped => DecodeWordSwapped(data, bigEndian).ToString(),
                SubrecordFieldType.Float => FormatFloat(EsmBinary.ReadSingle(data, bigEndian)),
                SubrecordFieldType.FormId => $"0x{EsmBinary.ReadUInt32(data, bigEndian):X8}",
                SubrecordFieldType.FormIdLittleEndian => $"0x{EsmBinary.ReadUInt32(data, false):X8}",
                SubrecordFieldType.UInt64 => EsmBinary.ReadUInt64(data, bigEndian).ToString(),
                SubrecordFieldType.Int64 => EsmBinary.ReadInt64(data, bigEndian).ToString(),
                SubrecordFieldType.Double => FormatDouble(EsmBinary.ReadDouble(data, bigEndian)),
                SubrecordFieldType.Vec3 => FormatVec3(data, bigEndian),
                SubrecordFieldType.Quaternion => FormatQuaternion(data, bigEndian),
                SubrecordFieldType.PosRot => FormatPosRot(data, bigEndian),
                SubrecordFieldType.ColorRgba => FormatColorRgba(data),
                SubrecordFieldType.ColorArgb => FormatColorArgb(data),
                SubrecordFieldType.String => Encoding.ASCII.GetString(data).TrimEnd('\0'),
                SubrecordFieldType.ByteArray => FormatBytes(data),
                SubrecordFieldType.Padding => $"[{data.Length} bytes padding]",
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
        var x = EsmBinary.ReadSingle(data, 0, bigEndian);
        var y = EsmBinary.ReadSingle(data, 4, bigEndian);
        var z = EsmBinary.ReadSingle(data, 8, bigEndian);
        return $"({x:F2}, {y:F2}, {z:F2})";
    }

    /// <summary>
    ///     Formats a Quaternion (4 floats: w, x, y, z).
    /// </summary>
    public static string FormatQuaternion(ReadOnlySpan<byte> data, bool bigEndian)
    {
        var w = EsmBinary.ReadSingle(data, 0, bigEndian);
        var x = EsmBinary.ReadSingle(data, 4, bigEndian);
        var y = EsmBinary.ReadSingle(data, 8, bigEndian);
        var z = EsmBinary.ReadSingle(data, 12, bigEndian);
        return $"({w:F3}, {x:F3}, {y:F3}, {z:F3})";
    }

    /// <summary>
    ///     Formats a PosRot (position + rotation, 6 floats).
    /// </summary>
    public static string FormatPosRot(ReadOnlySpan<byte> data, bool bigEndian)
    {
        var px = EsmBinary.ReadSingle(data, 0, bigEndian);
        var py = EsmBinary.ReadSingle(data, 4, bigEndian);
        var pz = EsmBinary.ReadSingle(data, 8, bigEndian);
        var rx = EsmBinary.ReadSingle(data, 12, bigEndian);
        var ry = EsmBinary.ReadSingle(data, 16, bigEndian);
        var rz = EsmBinary.ReadSingle(data, 20, bigEndian);
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
