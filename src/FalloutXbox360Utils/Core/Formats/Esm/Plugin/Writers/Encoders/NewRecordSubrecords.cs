namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Shared helpers for emitting subrecords in v4 new-record encoder paths. Each helper
///     produces an <see cref="EncodedSubrecord" /> with the appropriate byte payload —
///     avoids duplicating the same one-liner across every type-specific encoder.
/// </summary>
internal static class NewRecordSubrecords
{
    /// <summary>
    ///     Emit a null-terminated Latin-1 string subrecord (EDID, FULL, MODL, DESC, ...).
    /// </summary>
    public static EncodedSubrecord EncodeStringSubrecord(string signature, string value)
    {
        var byteCount = System.Text.Encoding.Latin1.GetByteCount(value);
        var buffer = new byte[byteCount + 1];
        System.Text.Encoding.Latin1.GetBytes(value, buffer);
        // Final byte already 0 (null terminator).
        return new EncodedSubrecord(signature, buffer);
    }

    /// <summary>Emit a 4-byte little-endian uint32 subrecord (FNAM, RNAM, ...).</summary>
    public static EncodedSubrecord EncodeUInt32Subrecord(string signature, uint value)
    {
        var bytes = new byte[4];
        SubrecordEncoder.WriteUInt32(bytes, 0, value);
        return new EncodedSubrecord(signature, bytes);
    }

    /// <summary>Emit a 4-byte little-endian int32 subrecord (DATA for int GMSTs, ...).</summary>
    public static EncodedSubrecord EncodeInt32Subrecord(string signature, int value)
    {
        var bytes = new byte[4];
        SubrecordEncoder.WriteInt32(bytes, 0, value);
        return new EncodedSubrecord(signature, bytes);
    }

    /// <summary>Emit a 4-byte little-endian float subrecord (FLTV, XCLW, ...).</summary>
    public static EncodedSubrecord EncodeFloatSubrecord(string signature, float value)
    {
        var bytes = new byte[4];
        SubrecordEncoder.WriteFloat(bytes, 0, value);
        return new EncodedSubrecord(signature, bytes);
    }

    /// <summary>Emit a 4-byte FormID subrecord (NAME, XOWN, XEZN, ...).</summary>
    public static EncodedSubrecord EncodeFormIdSubrecord(string signature, uint formId)
    {
        var bytes = new byte[4];
        SubrecordEncoder.WriteFormId(bytes, 0, formId);
        return new EncodedSubrecord(signature, bytes);
    }

    /// <summary>Emit a single-byte subrecord (FNAM for GLOB, DATA for CELL flags, ...).</summary>
    public static EncodedSubrecord EncodeByteSubrecord(string signature, byte value)
    {
        return new EncodedSubrecord(signature, [value]);
    }

    /// <summary>
    ///     Emit an opaque byte-array subrecord (MODT/MO2T/MO3T texture hashes, ...).
    ///     The schema marks these as unstructured byte arrays — no endian swap, no parsing.
    ///     The engine validates the bytes; we pass them through as-is.
    /// </summary>
    public static EncodedSubrecord EncodeByteArraySubrecord(string signature, byte[] data)
    {
        return new EncodedSubrecord(signature, data);
    }

    /// <summary>
    ///     Emit OBND — 12 bytes, 6 int16 values: X1, Y1, Z1, X2, Y2, Z2 (min/max bounds).
    ///     Per fopdoc, this is the canonical object-bounds layout for most record types.
    /// </summary>
    public static EncodedSubrecord EncodeObndSubrecord(Models.ObjectBounds bounds)
    {
        var data = new byte[12];
        SubrecordEncoder.WriteInt16(data, 0, bounds.X1);
        SubrecordEncoder.WriteInt16(data, 2, bounds.Y1);
        SubrecordEncoder.WriteInt16(data, 4, bounds.Z1);
        SubrecordEncoder.WriteInt16(data, 6, bounds.X2);
        SubrecordEncoder.WriteInt16(data, 8, bounds.Y2);
        SubrecordEncoder.WriteInt16(data, 10, bounds.Z2);
        return new EncodedSubrecord("OBND", data);
    }
}
