using System.Buffers.Binary;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

/// <summary>
///     Writes typed field values into PC little-endian ESM bytes.
///     Symmetric with <see cref="Conversion.Schema.SubrecordSchemaProcessor" /> which converts
///     Xbox big-endian bytes into PC little-endian bytes — this module instead constructs PC bytes
///     from in-memory model values.
/// </summary>
public static class SubrecordEncoder
{
    /// <summary>Subrecord header size (4-byte signature + 2-byte uint16 length).</summary>
    public const int HeaderSize = 6;

    /// <summary>
    ///     Writes a complete subrecord: 4-byte signature + 2-byte little-endian length + data bytes.
    ///     Throws if data length exceeds <see cref="ushort.MaxValue" /> — v1 does not emit XXXX-prefixed records.
    /// </summary>
    public static void WriteSubrecord(BinaryWriter writer, string signature, ReadOnlySpan<byte> data)
    {
        if (signature.Length != 4)
        {
            throw new ArgumentException($"Subrecord signature must be exactly 4 ASCII characters, got '{signature}'",
                nameof(signature));
        }

        if (data.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"Subrecord {signature} payload {data.Length} bytes exceeds 64KB without XXXX support.");
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        header[0] = (byte)signature[0];
        header[1] = (byte)signature[1];
        header[2] = (byte)signature[2];
        header[3] = (byte)signature[3];
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)data.Length);

        writer.Write(header);
        writer.Write(data);
    }

    /// <summary>
    ///     Writes a subrecord whose payload is a single 4-byte uint32.
    /// </summary>
    public static void WriteUInt32Subrecord(BinaryWriter writer, string signature, uint value)
    {
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        WriteSubrecord(writer, signature, data);
    }

    /// <summary>
    ///     Writes a subrecord whose payload is a single 4-byte int32.
    /// </summary>
    public static void WriteInt32Subrecord(BinaryWriter writer, string signature, int value)
    {
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(data, value);
        WriteSubrecord(writer, signature, data);
    }

    /// <summary>
    ///     Writes a subrecord whose payload is a single 4-byte float.
    /// </summary>
    public static void WriteFloatSubrecord(BinaryWriter writer, string signature, float value)
    {
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(data, value);
        WriteSubrecord(writer, signature, data);
    }

    /// <summary>
    ///     Writes a subrecord whose payload is a single 4-byte FormID.
    /// </summary>
    public static void WriteFormIdSubrecord(BinaryWriter writer, string signature, uint formId)
    {
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, formId);
        WriteSubrecord(writer, signature, data);
    }

    /// <summary>
    ///     Writes a subrecord whose payload is a single byte.
    /// </summary>
    public static void WriteByteSubrecord(BinaryWriter writer, string signature, byte value)
    {
        Span<byte> data = stackalloc byte[1];
        data[0] = value;
        WriteSubrecord(writer, signature, data);
    }

    /// <summary>
    ///     Writes a null-terminated string subrecord. The bytes are written in ISO-8859-1
    ///     (Latin-1), which is byte-compatible with Windows-1252 for ASCII (U+0000–U+007F) and
    ///     most extended Latin characters (U+00A0–U+00FF). Plugin metadata strings (CNAM/SNAM)
    ///     and EDID identifiers are typically pure ASCII, so this is safe in practice.
    /// </summary>
    public static void WriteStringSubrecord(BinaryWriter writer, string signature, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            // Empty string is encoded as a single null terminator.
            Span<byte> nul = stackalloc byte[1];
            nul[0] = 0;
            WriteSubrecord(writer, signature, nul);
            return;
        }

        var byteCount = Encoding.Latin1.GetByteCount(value);
        var buffer = new byte[byteCount + 1];
        Encoding.Latin1.GetBytes(value, buffer);
        // Final byte is already 0 (default-initialized).
        WriteSubrecord(writer, signature, buffer);
    }

    /// <summary>
    ///     Writes a typed value into a target span at the given offset, in PC little-endian.
    ///     Used by encoders that need to patch specific fields inside a packed binary subrecord
    ///     (e.g., DATA, DNAM).
    /// </summary>
    public static void WriteUInt32(Span<byte> dest, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(offset, 4), value);
    }

    public static void WriteInt32(Span<byte> dest, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(offset, 4), value);
    }

    public static void WriteUInt16(Span<byte> dest, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(offset, 2), value);
    }

    public static void WriteInt16(Span<byte> dest, int offset, short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(dest.Slice(offset, 2), value);
    }

    public static void WriteFloat(Span<byte> dest, int offset, float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(offset, 4), value);
    }

    public static void WriteFormId(Span<byte> dest, int offset, uint formId)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(offset, 4), formId);
    }
}
