using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;

/// <summary>
///     Authenticates the exact CLAS schema observed in every one of the 52 bounded retail SUNP
///     streams. The generic reflection reader validates a stream against its own declarations;
///     this validator prevents a changed or adversarial declaration from redefining the typed
///     projection. Audited form tokens, flags, field order, types, and runtime-offset words must all
///     agree; the offsets are authenticated as metadata but are never used as serialized layout.
/// </summary>
internal static class StarfieldSunPresetSchemaValidator
{
    internal const string RootType = "BSGalaxy::BGSSunPresetForm";
    internal const string NightType = "BSGalaxy::BGSSunPresetForm::NightSettings";
    internal const string DawnDuskType = "BSGalaxy::BGSSunPresetForm::DawnDuskSettings";
    internal const string Float4Type = "XMFLOAT4";

    private const uint ChunkBeth = 0x48544542; // BETH
    private const uint ChunkStrt = 0x54525453; // STRT
    private const uint ChunkType = 0x45505954; // TYPE
    private const uint ChunkClas = 0x53414C43; // CLAS
    private const uint ChunkObjt = 0x544A424F; // OBJT
    private const uint ChunkDiff = 0x46464944; // DIFF
    private const uint TypeString = 0xFFFFFF02;
    private const uint TypeRef = 0xFFFFFF05;
    private const uint TypeFloat = 0xFFFFFF11;
    private const int HeaderSize = 24;

    // Retail class order is stable across all seven REFL and 45 RDIF streams. Field order is
    // serialization order for OBJT and the authoritative UInt16 index space for DIFF.
    private static readonly ExpectedClass[] ExpectedClasses =
    [
        new(
            NightType,
            0,
            [
                new("DirectionalColor", Float4Type, null, 0),
                new("DirectionalIlluminance", null, TypeFloat, 16),
                new("GlareColor", Float4Type, null, 20)
            ]),
        new(
            RootType,
            0,
            [
                new("pParent", null, TypeRef, 280),
                new("SunColor", Float4Type, null, 288),
                new("SunIlluminance", null, TypeFloat, 304),
                new("SunGlareColor", Float4Type, null, 308),
                new("SunDiskTexture", null, TypeString, 328),
                new("SunDiskScreenSizeMin", null, TypeFloat, 336),
                new("SunDiskScreenSizeMax", null, TypeFloat, 340),
                new("DuskDawnPreset", DawnDuskType, null, 344),
                new("NightPreset", NightType, null, 368)
            ]),
        new(
            Float4Type,
            8,
            [
                new("x", null, TypeFloat, 0),
                new("y", null, TypeFloat, 4),
                new("z", null, TypeFloat, 8),
                new("w", null, TypeFloat, 12)
            ]),
        new(
            DawnDuskType,
            0,
            [
                new("DirectionalColor", Float4Type, null, 0),
                new("TransitionStartAngle", null, TypeFloat, 16),
                new("TransitionEndAngle", null, TypeFloat, 20)
            ])
    ];

    internal static bool TryValidate(
        ReadOnlySpan<byte> data,
        bool expectDiff,
        out string? error)
    {
        error = null;
        if (data.Length < HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(data) != ChunkBeth ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != 4 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) != 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) != ChunkStrt)
        {
            error = "SUNP reflection header is not the exact bounded BETH-v4 envelope.";
            return false;
        }

        var stringByteCount = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        if (stringByteCount > (uint)(data.Length - HeaderSize) ||
            !TryReadStringTable(
                data.Slice(HeaderSize, (int)stringByteCount), out var strings))
        {
            error = "SUNP reflection string table is malformed or unterminated.";
            return false;
        }

        var position = HeaderSize + (int)stringByteCount;
        if (!TryReadChunk(data, ref position, out var chunkType, out var body) ||
            chunkType != ChunkType ||
            body.Length != sizeof(uint) ||
            BinaryPrimitives.ReadUInt32LittleEndian(body) != (uint)ExpectedClasses.Length)
        {
            error = "SUNP reflection TYPE chunk does not declare the exact four-class schema.";
            return false;
        }

        foreach (var expectedClass in ExpectedClasses)
        {
            if (!TryReadChunk(data, ref position, out chunkType, out body) ||
                chunkType != ChunkClas ||
                !TryValidateClass(body, strings, expectedClass, out error))
            {
                error ??= $"SUNP reflection is missing CLAS '{expectedClass.Name}' in retail order.";
                return false;
            }
        }

        var expectedObjectChunk = expectDiff ? ChunkDiff : ChunkObjt;
        if (!TryReadChunk(data, ref position, out chunkType, out body) ||
            chunkType != expectedObjectChunk ||
            body.Length < sizeof(uint) ||
            !TryResolveName(BinaryPrimitives.ReadUInt32LittleEndian(body), strings, out var rootType) ||
            !string.Equals(rootType, RootType, StringComparison.Ordinal))
        {
            error = expectDiff
                ? "SUNP reflection has no exact DIFF root payload."
                : "SUNP reflection has no exact OBJT root payload.";
            return false;
        }

        if (position != data.Length)
        {
            error = "SUNP reflection contains an extra, missing, or trailing chunk.";
            return false;
        }

        return true;
    }

    private static bool TryValidateClass(
        ReadOnlySpan<byte> body,
        IReadOnlyDictionary<uint, string> strings,
        ExpectedClass expected,
        out string? error)
    {
        error = null;
        if (body.Length < 12 ||
            !TryResolveName(BinaryPrimitives.ReadUInt32LittleEndian(body), strings, out var className) ||
            !string.Equals(className, expected.Name, StringComparison.Ordinal) ||
            !TryResolveName(BinaryPrimitives.ReadUInt32LittleEndian(body[4..]), strings, out var formType) ||
            !string.Equals(formType, RootType, StringComparison.Ordinal))
        {
            error = $"SUNP CLAS is not exact class '{expected.Name}' with form '{RootType}'.";
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(body[8..]);
        var fieldCount = BinaryPrimitives.ReadUInt16LittleEndian(body[10..]);
        if (flags != expected.Flags ||
            fieldCount != expected.Fields.Count ||
            body.Length != 12 + (fieldCount * 12))
        {
            error = $"SUNP CLAS '{expected.Name}' has changed flags, field count, or framing.";
            return false;
        }

        for (var index = 0; index < expected.Fields.Count; index++)
        {
            var descriptor = body.Slice(12 + (index * 12), 12);
            var expectedField = expected.Fields[index];
            if (!TryResolveName(
                    BinaryPrimitives.ReadUInt32LittleEndian(descriptor), strings, out var fieldName) ||
                !string.Equals(fieldName, expectedField.Name, StringComparison.Ordinal))
            {
                error = $"SUNP CLAS '{expected.Name}' field index {index} is not " +
                        $"'{expectedField.Name}'.";
                return false;
            }

            var typeToken = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
            if (expectedField.BuiltInType is { } expectedBuiltIn)
            {
                if (typeToken != expectedBuiltIn)
                {
                    error = $"SUNP field '{expected.Name}.{expectedField.Name}' has changed type.";
                    return false;
                }
            }
            else if (!TryResolveName(typeToken, strings, out var fieldType) ||
                     !string.Equals(fieldType, expectedField.NamedType, StringComparison.Ordinal))
            {
                error = $"SUNP field '{expected.Name}.{expectedField.Name}' has changed object type.";
                return false;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]) !=
                expectedField.RuntimeOffset)
            {
                error = $"SUNP field '{expected.Name}.{expectedField.Name}' has changed " +
                        "runtime-offset metadata.";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadStringTable(
        ReadOnlySpan<byte> body,
        out Dictionary<uint, string> strings)
    {
        strings = new Dictionary<uint, string>();
        var position = 0;
        while (position < body.Length)
        {
            var terminator = body[position..].IndexOf((byte)0);
            if (terminator < 0)
            {
                return false;
            }

            var bytes = body.Slice(position, terminator);
            for (var index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] > 0x7F)
                {
                    return false;
                }
            }

            strings.Add((uint)position, Encoding.ASCII.GetString(bytes));
            position += terminator + 1;
        }

        return position == body.Length;
    }

    private static bool TryReadChunk(
        ReadOnlySpan<byte> data,
        ref int position,
        out uint type,
        out ReadOnlySpan<byte> body)
    {
        type = 0;
        body = default;
        if (position < 0 || position > data.Length - 8)
        {
            return false;
        }

        type = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
        if (length > (uint)(data.Length - position - 8))
        {
            return false;
        }

        body = data.Slice(position + 8, (int)length);
        position += 8 + (int)length;
        return true;
    }

    private static bool TryResolveName(
        uint token,
        IReadOnlyDictionary<uint, string> strings,
        out string name) =>
        strings.TryGetValue(token, out name!);

    private sealed record ExpectedClass(
        string Name,
        ushort Flags,
        IReadOnlyList<ExpectedField> Fields);

    private sealed record ExpectedField(
        string Name,
        string? NamedType,
        uint? BuiltInType,
        uint RuntimeOffset);
}
