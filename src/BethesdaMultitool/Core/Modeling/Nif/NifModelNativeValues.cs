using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Turns decoded NIF values into bounded native-state JSON (plan section 3, "Common to every block"). Values stay
///     exact: integers at their stored width, floats as the shortest round-trip decimal (non-finite floats as their
///     IEEE bits in hex, since JSON has no NaN or infinity), strings as Latin-1 text with the raw bytes in hex whenever a
///     byte is at or above 0x80. Arrays with more than <see cref="MaximumInlineElements" /> elements are summarized as
///     their count plus the SHA-256 of their canonical encoding (<see cref="CanonicalEncoding" />).
/// </summary>
/// <remarks>
///     The canonical encoding (version 1) is, per value, a one-byte tag and little-endian fields: integer 'I' + width +
///     signedness + the 8-byte raw bits; float 'F' + 4-byte bits; half 'H' + 2-byte bits; string 'S' + index + length
///     (-1 when unresolved) + raw bytes; sized string 'Z' + length + raw bytes; reference 'R' (Ref) or 'P' (Ptr) + index;
///     struct 'T' + field count, then per field the UTF-8 name length, name, ordinal and value; array 'A' + rows flag +
///     count + items; bulk float 'f', ushort 'u' and byte 'b' arrays + components per element + element count + the
///     stored components. Integers inside the encoding are 32-bit little-endian.
/// </remarks>
internal static class NifModelNativeValues
{
    /// <summary>Arrays with more elements than this are summarized.</summary>
    public const int MaximumInlineElements = 64;

    /// <summary>Byte strings longer than this are summarized as length plus SHA-256.</summary>
    public const int MaximumInlineTextBytes = 1024;

    /// <summary>The name of the canonical value encoding that summary digests are computed over.</summary>
    public const string CanonicalEncoding = "bmt.nif.canonical-values/1";

    /// <summary>The name recorded for digests computed over plain raw bytes.</summary>
    public const string RawBytesEncoding = "raw-bytes";

    private static readonly byte[] NulSeparator = [0];

    /// <summary>Converts one decoded value to JSON, summarizing large arrays.</summary>
    public static JsonNode? ToJson(NifValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        switch (value)
        {
            case NifIntegerValue integer:
                return !integer.IsSigned && integer.ByteWidth == 8 && integer.RawBits > long.MaxValue
                    ? JsonValue.Create(integer.RawBits)
                    : JsonValue.Create(integer.Value);
            case NifFloatValue single:
                return Float(single.Value);
            case NifHalfFloatValue half:
                var widened = half.ToSingle();
                return float.IsFinite(widened)
                    ? JsonValue.Create(widened)
                    : JsonValue.Create(string.Create(CultureInfo.InvariantCulture, $"0x{half.RawBits:X4}"))!;
            case NifStringValue text:
                return StringReference(text);
            case NifSizedStringValue sized:
                return Text(sized.RawBytes.Span);
            case NifRefValue reference:
                return JsonValue.Create(reference.Index);
            case NifStructValue structure:
                return Struct(structure);
            case NifArrayValue array:
                return array.Count > MaximumInlineElements
                    ? Summary(array.Count, Sha256(array), CanonicalEncoding)
                    : new JsonArray(array.Items.Select(ToJson).ToArray());
            case NifFloatArrayValue floats:
                return FloatArray(floats);
            case NifUInt16ArrayValue shorts:
                return UInt16Array(shorts);
            case NifByteArrayValue bytes:
                return bytes.Count > MaximumInlineElements
                    ? Summary(bytes.Count, Sha256(bytes), CanonicalEncoding)
                    : JsonValue.Create(Convert.ToHexStringLower(bytes.Bytes.Span));
            default:
                throw new InvalidOperationException($"Unhandled NIF value shape {value.GetType().Name}.");
        }
    }

    /// <summary>A float as a JSON number, or its IEEE bits in hex when it is not finite.</summary>
    public static JsonNode Float(float value)
    {
        return float.IsFinite(value)
            ? JsonValue.Create(value)
            : JsonValue.Create(string.Create(CultureInfo.InvariantCulture,
                $"0x{BitConverter.SingleToUInt32Bits(value):X8}"))!;
    }

    /// <summary>A double as a JSON number, or its IEEE bits in hex when it is not finite.</summary>
    public static JsonNode Double(double value)
    {
        return double.IsFinite(value)
            ? JsonValue.Create(value)
            : JsonValue.Create(string.Create(CultureInfo.InvariantCulture,
                $"0x{BitConverter.DoubleToUInt64Bits(value):X16}"))!;
    }

    /// <summary>An integer list inline, or its count plus the SHA-256 of its 32-bit little-endian encoding.</summary>
    public static JsonNode Integers(IReadOnlyList<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count <= MaximumInlineElements)
        {
            return new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }

        var bytes = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return Summary(values.Count, Convert.ToHexStringLower(SHA256.HashData(bytes)), "int32-le");
    }

    /// <summary>A text list inline, or its count plus the SHA-256 of the UTF-8 entries each followed by a NUL.</summary>
    public static JsonNode Texts(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count <= MaximumInlineElements)
        {
            return new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData(NulSeparator);
        }

        return Summary(values.Count, Convert.ToHexStringLower(hash.GetHashAndReset()), "utf8-nul-separated");
    }

    /// <summary>
    ///     Stored bytes as <c>{"text": Latin-1}</c>, plus <c>rawHex</c> when a byte is at or above 0x80; bytes longer
    ///     than <see cref="MaximumInlineTextBytes" /> become their length plus SHA-256.
    /// </summary>
    public static JsonNode Text(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > MaximumInlineTextBytes)
        {
            return Summary(raw.Length, Convert.ToHexStringLower(SHA256.HashData(raw)), RawBytesEncoding);
        }

        var node = new JsonObject { ["text"] = Encoding.Latin1.GetString(raw) };
        if (raw.IndexOfAnyInRange((byte)0x80, (byte)0xFF) >= 0)
        {
            node["rawHex"] = Convert.ToHexStringLower(raw);
        }

        return node;
    }

    /// <summary>A summary object: the element count, the digest and what the digest was computed over.</summary>
    public static JsonObject Summary(int count, string sha256, string hashOf)
    {
        return new JsonObject { ["count"] = count, ["sha256"] = sha256, ["hashOf"] = hashOf };
    }

    /// <summary>The SHA-256 of a value's canonical encoding (see the type remarks).</summary>
    public static string Sha256(NifValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, value);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static JsonNode? StringReference(NifStringValue value)
    {
        if (value.IsNone)
        {
            return null;
        }

        var node = new JsonObject { ["index"] = value.Index };
        if (!value.IsResolved)
        {
            node["text"] = null;
            return node;
        }

        var raw = value.RawBytes.Span;
        if (raw.Length > MaximumInlineTextBytes)
        {
            node["summary"] = Summary(raw.Length, Convert.ToHexStringLower(SHA256.HashData(raw)), RawBytesEncoding);
            return node;
        }

        node["text"] = Encoding.Latin1.GetString(raw);
        if (value.HasNonAsciiBytes)
        {
            node["rawHex"] = Convert.ToHexStringLower(raw);
        }

        return node;
    }

    private static JsonObject Struct(NifStructValue value)
    {
        var node = new JsonObject();
        foreach (var field in value.Fields)
        {
            var key = field.Ordinal == 0
                ? field.Name
                : string.Create(CultureInfo.InvariantCulture, $"{field.Name}#{field.Ordinal}");
            node[key] = ToJson(field.Value);
        }

        return node;
    }

    private static JsonNode FloatArray(NifFloatArrayValue value)
    {
        if (value.Count > MaximumInlineElements)
        {
            return Summary(value.Count, Sha256(value), CanonicalEncoding);
        }

        var elements = new JsonNode?[value.Count];
        for (var element = 0; element < value.Count; element++)
        {
            if (value.ComponentsPerElement == 1)
            {
                elements[element] = Float(value.Get(element));
                continue;
            }

            var components = new JsonNode?[value.ComponentsPerElement];
            for (var component = 0; component < components.Length; component++)
            {
                components[component] = Float(value.Get(element, component));
            }

            elements[element] = new JsonArray(components);
        }

        return new JsonArray(elements);
    }

    private static JsonNode UInt16Array(NifUInt16ArrayValue value)
    {
        if (value.Count > MaximumInlineElements)
        {
            return Summary(value.Count, Sha256(value), CanonicalEncoding);
        }

        var elements = new JsonNode?[value.Count];
        for (var element = 0; element < value.Count; element++)
        {
            if (value.ComponentsPerElement == 1)
            {
                elements[element] = JsonValue.Create((int)value.Get(element));
                continue;
            }

            var components = new JsonNode?[value.ComponentsPerElement];
            for (var component = 0; component < components.Length; component++)
            {
                components[component] = JsonValue.Create((int)value.Get(element, component));
            }

            elements[element] = new JsonArray(components);
        }

        return new JsonArray(elements);
    }

    private static void Append(IncrementalHash hash, NifValue value)
    {
        Span<byte> scratch = stackalloc byte[16];
        switch (value)
        {
            case NifIntegerValue integer:
                scratch[0] = (byte)'I';
                scratch[1] = (byte)integer.ByteWidth;
                scratch[2] = integer.IsSigned ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteUInt64LittleEndian(scratch[3..], integer.RawBits);
                hash.AppendData(scratch[..11]);
                break;
            case NifFloatValue single:
                scratch[0] = (byte)'F';
                BinaryPrimitives.WriteUInt32LittleEndian(scratch[1..], single.RawBits);
                hash.AppendData(scratch[..5]);
                break;
            case NifHalfFloatValue half:
                scratch[0] = (byte)'H';
                BinaryPrimitives.WriteUInt16LittleEndian(scratch[1..], half.RawBits);
                hash.AppendData(scratch[..3]);
                break;
            case NifStringValue text:
                scratch[0] = (byte)'S';
                BinaryPrimitives.WriteInt32LittleEndian(scratch[1..], text.Index);
                BinaryPrimitives.WriteInt32LittleEndian(scratch[5..], text.IsResolved ? text.RawBytes.Length : -1);
                hash.AppendData(scratch[..9]);
                hash.AppendData(text.RawBytes.Span);
                break;
            case NifSizedStringValue sized:
                scratch[0] = (byte)'Z';
                BinaryPrimitives.WriteInt32LittleEndian(scratch[1..], sized.RawBytes.Length);
                hash.AppendData(scratch[..5]);
                hash.AppendData(sized.RawBytes.Span);
                break;
            case NifRefValue reference:
                scratch[0] = reference.IsPointer ? (byte)'P' : (byte)'R';
                BinaryPrimitives.WriteInt32LittleEndian(scratch[1..], reference.Index);
                hash.AppendData(scratch[..5]);
                break;
            case NifStructValue structure:
                scratch[0] = (byte)'T';
                BinaryPrimitives.WriteInt32LittleEndian(scratch[1..], structure.Fields.Count);
                hash.AppendData(scratch[..5]);
                foreach (var field in structure.Fields)
                {
                    var name = Encoding.UTF8.GetBytes(field.Name);
                    BinaryPrimitives.WriteInt32LittleEndian(scratch, name.Length);
                    hash.AppendData(scratch[..4]);
                    hash.AppendData(name);
                    BinaryPrimitives.WriteInt32LittleEndian(scratch, field.Ordinal);
                    hash.AppendData(scratch[..4]);
                    Append(hash, field.Value);
                }

                break;
            case NifArrayValue array:
                scratch[0] = (byte)'A';
                scratch[1] = array.IsRows ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteInt32LittleEndian(scratch[2..], array.Count);
                hash.AppendData(scratch[..6]);
                foreach (var item in array.Items)
                {
                    Append(hash, item);
                }

                break;
            case NifFloatArrayValue floats:
                AppendBulkHeader(hash, scratch, 'f', floats.ComponentsPerElement, floats.Count);
                AppendLittleEndian(hash, floats.Bits);
                break;
            case NifUInt16ArrayValue shorts:
                AppendBulkHeader(hash, scratch, 'u', shorts.ComponentsPerElement, shorts.Count);
                AppendLittleEndian(hash, shorts.Values);
                break;
            case NifByteArrayValue bytes:
                AppendBulkHeader(hash, scratch, 'b', 1, bytes.Count);
                hash.AppendData(bytes.Bytes.Span);
                break;
            default:
                throw new InvalidOperationException($"Unhandled NIF value shape {value.GetType().Name}.");
        }
    }

    private static void AppendBulkHeader(IncrementalHash hash, Span<byte> scratch, char tag, int components, int count)
    {
        scratch[0] = (byte)tag;
        BinaryPrimitives.WriteInt32LittleEndian(scratch[1..], components);
        BinaryPrimitives.WriteInt32LittleEndian(scratch[5..], count);
        hash.AppendData(scratch[..9]);
    }

    private static void AppendLittleEndian(IncrementalHash hash, ReadOnlySpan<uint> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            hash.AppendData(MemoryMarshal.AsBytes(values));
            return;
        }

        Span<byte> buffer = stackalloc byte[4];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            hash.AppendData(buffer);
        }
    }

    private static void AppendLittleEndian(IncrementalHash hash, ReadOnlySpan<ushort> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            hash.AppendData(MemoryMarshal.AsBytes(values));
            return;
        }

        Span<byte> buffer = stackalloc byte[2];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            hash.AppendData(buffer);
        }
    }
}
