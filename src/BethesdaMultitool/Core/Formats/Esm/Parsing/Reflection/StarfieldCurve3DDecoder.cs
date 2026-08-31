using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;

/// <summary>
///     Strict standalone decoder for Starfield CUR3's version-4 BETH stream. CUR3 uses a custom
///     out-of-line USER serializer for each <c>BSFloatCurve</c>, paired immediately with its LIST
///     of controls, so the generic reflection reader intentionally cannot project this shape.
/// </summary>
internal static class StarfieldCurve3DDecoder
{
    internal const string RootType = "BGSCurve3DForm";
    internal const string Curve3DType = "BSFloat3DCurve";
    internal const string FloatCurveType = "BSFloatCurve";
    internal const string ControlType = "BSFloatCurve::Control";

    private const uint ChunkBeth = 0x48544542; // BETH
    private const uint ChunkStrt = 0x54525453; // STRT
    private const uint ChunkType = 0x45505954; // TYPE
    private const uint ChunkClas = 0x53414C43; // CLAS
    private const uint ChunkObjt = 0x544A424F; // OBJT
    private const uint ChunkUser = 0x52455355; // USER
    private const uint ChunkList = 0x5453494C; // LIST
    private const int HeaderSize = 24;
    private const uint ExpectedTotalChunkCount = 14;

    private const uint TypeString = 0xFFFFFF02;
    private const uint TypeList = 0xFFFFFF03;
    private const uint TypeBool = 0xFFFFFF10;
    private const uint TypeFloat = 0xFFFFFF11;

    private static readonly string[] ExpectedStrings =
    [
        RootType,
        Curve3DType,
        FloatCurveType,
        "Controls",
        "MaxInput",
        "MinInput",
        "InputDistance",
        "MaxValue",
        "MinValue",
        "DefaultValue",
        "Type",
        "Edge",
        "IsSampleInterpolating",
        "XCurve",
        "YCurve",
        "ZCurve",
        "Curve",
        ControlType,
        "Input",
        "Value"
    ];

    private static readonly ExpectedClass[] ExpectedClasses =
    [
        new(
            Curve3DType,
            0,
            [
                NamedField("XCurve", FloatCurveType, 4_194_304),
                NamedField("YCurve", FloatCurveType, 4_194_368),
                NamedField("ZCurve", FloatCurveType, 4_194_432)
            ]),
        new(
            FloatCurveType,
            0x000C,
            [
                BuiltInField("Controls", TypeList, 1_572_872),
                BuiltInField("MaxInput", TypeFloat, 262_176),
                BuiltInField("MinInput", TypeFloat, 262_180),
                BuiltInField("InputDistance", TypeFloat, 262_184),
                BuiltInField("MaxValue", TypeFloat, 262_188),
                BuiltInField("MinValue", TypeFloat, 262_192),
                BuiltInField("DefaultValue", TypeFloat, 262_196),
                BuiltInField("Type", TypeString, 65_592),
                BuiltInField("Edge", TypeString, 65_593),
                BuiltInField("IsSampleInterpolating", TypeBool, 65_594)
            ]),
        new(
            RootType,
            0,
            [NamedField("Curve", Curve3DType, 12_583_192)]),
        new(
            ControlType,
            0,
            [
                BuiltInField("Input", TypeFloat, 262_144),
                BuiltInField("Value", TypeFloat, 262_148)
            ])
    ];

    internal static bool TryDecode(
        ReadOnlySpan<byte> data,
        out StarfieldCurve3DDefinition? definition,
        out string? error)
    {
        definition = null;
        error = null;
        if (data.Length < HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(data) != ChunkBeth ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != 4 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) != ExpectedTotalChunkCount ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) != ChunkStrt)
        {
            error = "CUR3 reflection does not have the exact version-4 BETH envelope.";
            return false;
        }

        var stringByteCount = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        if (stringByteCount > (uint)(data.Length - HeaderSize) ||
            !TryReadStringTable(
                data.Slice(HeaderSize, (int)stringByteCount), out var strings, out error))
        {
            error ??= "CUR3 reflection has an invalid string table.";
            return false;
        }

        var position = HeaderSize + (int)stringByteCount;
        if (!TryReadChunk(data, ref position, ChunkType, out var body, out error) ||
            body.Length != sizeof(uint) ||
            BinaryPrimitives.ReadUInt32LittleEndian(body) != (uint)ExpectedClasses.Length)
        {
            error ??= "CUR3 TYPE does not declare the exact four-class schema.";
            return false;
        }

        foreach (var expectedClass in ExpectedClasses)
        {
            if (!TryReadChunk(data, ref position, ChunkClas, out body, out error) ||
                !TryValidateClass(body, strings, expectedClass, out error))
            {
                error ??= $"CUR3 is missing CLAS '{expectedClass.Name}' in retail order.";
                return false;
            }
        }

        if (!TryReadChunk(data, ref position, ChunkObjt, out body, out error) ||
            body.Length != sizeof(uint) ||
            !TryResolveName(BinaryPrimitives.ReadUInt32LittleEndian(body), strings, out var root) ||
            !string.Equals(root, RootType, StringComparison.Ordinal))
        {
            error ??= "CUR3 has no exact BGSCurve3DForm OBJT root.";
            return false;
        }

        var axes = new StarfieldFloatCurve[3];
        for (var axis = 0; axis < axes.Length; axis++)
        {
            if (!TryReadChunk(data, ref position, ChunkUser, out var userBody, out error) ||
                !TryReadCurveMetadata(userBody, strings, out var metadata, out error) ||
                !TryReadChunk(data, ref position, ChunkList, out var listBody, out error) ||
                !TryReadControls(listBody, strings, out var controls, out error))
            {
                error = $"CUR3 {AxisName(axis)} axis is malformed: {error ?? "unknown USER/LIST layout"}";
                return false;
            }

            axes[axis] = new StarfieldFloatCurve
            {
                MaxInput = metadata.MaxInput,
                MinInput = metadata.MinInput,
                InputDistance = metadata.InputDistance,
                MaxValue = metadata.MaxValue,
                MinValue = metadata.MinValue,
                DefaultValue = metadata.DefaultValue,
                CurveType = metadata.CurveType,
                EdgeMode = metadata.EdgeMode,
                IsSampleInterpolating = metadata.IsSampleInterpolating,
                SerializedControlListMarker = metadata.SerializedControlListMarker,
                Controls = controls,
                RawSerializedMetadata = userBody[8..].ToArray(),
                RawControlListBody = listBody.ToArray()
            };
        }

        if (position != data.Length)
        {
            error = "CUR3 contains an extra, duplicate, or trailing reflection chunk.";
            return false;
        }

        definition = new StarfieldCurve3DDefinition(axes[0], axes[1], axes[2]);
        return true;
    }

    private static bool TryReadStringTable(
        ReadOnlySpan<byte> body,
        out IReadOnlyDictionary<uint, string> strings,
        out string? error)
    {
        var result = new Dictionary<uint, string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        while (position < body.Length)
        {
            var terminator = body[position..].IndexOf((byte)0);
            if (terminator <= 0)
            {
                strings = result;
                error = "CUR3 string table contains an empty or unterminated name.";
                return false;
            }

            var bytes = body.Slice(position, terminator);
            if (ContainsNonAscii(bytes))
            {
                strings = result;
                error = "CUR3 string table contains a non-ASCII name.";
                return false;
            }

            var name = Encoding.ASCII.GetString(bytes);
            if (!names.Add(name))
            {
                strings = result;
                error = $"CUR3 string table duplicates name '{name}'.";
                return false;
            }

            result.Add((uint)position, name);
            position += terminator + 1;
        }

        if (position != body.Length ||
            names.Count != ExpectedStrings.Length ||
            ExpectedStrings.Any(name => !names.Contains(name)))
        {
            strings = result;
            error = "CUR3 string table does not contain the exact retail schema names.";
            return false;
        }

        strings = result;
        error = null;
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
            !TryResolveName(BinaryPrimitives.ReadUInt32LittleEndian(body[4..]), strings, out var formName) ||
            !string.Equals(formName, RootType, StringComparison.Ordinal))
        {
            error = $"CUR3 CLAS is not exact class '{expected.Name}' with form '{RootType}'.";
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(body[8..]);
        var fieldCount = BinaryPrimitives.ReadUInt16LittleEndian(body[10..]);
        if (flags != expected.Flags ||
            fieldCount != expected.Fields.Count ||
            body.Length != 12 + (fieldCount * 12))
        {
            error = $"CUR3 CLAS '{expected.Name}' has changed flags, field count, or framing.";
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
                error = $"CUR3 CLAS '{expected.Name}' field {index} is not '{expectedField.Name}'.";
                return false;
            }

            var typeToken = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
            if (expectedField.NamedType is { } namedType)
            {
                if (!TryResolveName(typeToken, strings, out var actualType) ||
                    !string.Equals(actualType, namedType, StringComparison.Ordinal))
                {
                    error = $"CUR3 field '{expected.Name}.{expectedField.Name}' has changed type.";
                    return false;
                }
            }
            else if (typeToken != expectedField.BuiltInType)
            {
                error = $"CUR3 field '{expected.Name}.{expectedField.Name}' has changed type.";
                return false;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]) !=
                expectedField.RuntimeOffset)
            {
                error = $"CUR3 field '{expected.Name}.{expectedField.Name}' has changed " +
                        "runtime-offset metadata.";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadCurveMetadata(
        ReadOnlySpan<byte> body,
        IReadOnlyDictionary<uint, string> strings,
        out CurveMetadata metadata,
        out string? error)
    {
        metadata = default;
        error = null;
        var position = 0;
        if (!TryReadNamedToken(body, ref position, strings, FloatCurveType) ||
            !TryReadNamedToken(body, ref position, strings, FloatCurveType))
        {
            error = "USER does not declare and serialize exact type BSFloatCurve.";
            return false;
        }

        if (!TryReadFiniteFloat(body, ref position, out var maxInput) ||
            !TryReadFiniteFloat(body, ref position, out var minInput) ||
            !TryReadFiniteFloat(body, ref position, out var inputDistance) ||
            !TryReadFiniteFloat(body, ref position, out var maxValue) ||
            !TryReadFiniteFloat(body, ref position, out var minValue) ||
            !TryReadFiniteFloat(body, ref position, out var defaultValue))
        {
            error = "USER contains truncated or non-finite BSFloatCurve scalar metadata.";
            return false;
        }

        if (!TryReadReflectedString(body, ref position, out var curveType) ||
            !TryReadReflectedString(body, ref position, out var edgeMode))
        {
            error = "USER contains a malformed Type or Edge string.";
            return false;
        }

        if (position >= body.Length || body[position] > 1)
        {
            error = "USER IsSampleInterpolating is missing or is not a reflected Bool.";
            return false;
        }

        var isSampleInterpolating = body[position++] != 0;
        if (!TryReadUInt32(body, ref position, out var controlListMarker) || position != body.Length)
        {
            error = "USER has truncated or trailing serializer metadata.";
            return false;
        }

        metadata = new CurveMetadata(
            maxInput,
            minInput,
            inputDistance,
            maxValue,
            minValue,
            defaultValue,
            curveType,
            edgeMode,
            isSampleInterpolating,
            controlListMarker);
        return true;
    }

    private static bool TryReadControls(
        ReadOnlySpan<byte> body,
        IReadOnlyDictionary<uint, string> strings,
        out IReadOnlyList<StarfieldFloatCurveControl> controls,
        out string? error)
    {
        controls = [];
        error = null;
        var position = 0;
        if (!TryReadNamedToken(body, ref position, strings, ControlType) ||
            !TryReadUInt32(body, ref position, out var count) ||
            count > int.MaxValue ||
            8L + (count * 8L) != body.Length)
        {
            error = "LIST is not an exactly framed List<BSFloatCurve::Control>.";
            return false;
        }

        var result = new StarfieldFloatCurveControl[(int)count];
        for (var index = 0; index < result.Length; index++)
        {
            if (!TryReadFiniteFloat(body, ref position, out var input) ||
                !TryReadFiniteFloat(body, ref position, out var value))
            {
                error = $"LIST control {index} is truncated or non-finite.";
                return false;
            }

            result[index] = new StarfieldFloatCurveControl(input, value);
        }

        if (position != body.Length)
        {
            error = "LIST has trailing unread control bytes.";
            return false;
        }

        controls = Array.AsReadOnly(result);
        return true;
    }

    private static bool TryReadChunk(
        ReadOnlySpan<byte> data,
        ref int position,
        uint expectedType,
        out ReadOnlySpan<byte> body,
        out string? error)
    {
        body = default;
        error = null;
        if (position < 0 || position > data.Length - 8)
        {
            error = "Reflection chunk header is truncated.";
            return false;
        }

        var actualType = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
        if (actualType != expectedType)
        {
            error = $"Expected {ChunkName(expectedType)} chunk but found {ChunkName(actualType)}.";
            return false;
        }

        if (length > (uint)(data.Length - position - 8))
        {
            error = $"{ChunkName(expectedType)} chunk body is truncated.";
            return false;
        }

        body = data.Slice(position + 8, (int)length);
        position += 8 + (int)length;
        return true;
    }

    private static bool TryReadNamedToken(
        ReadOnlySpan<byte> body,
        ref int position,
        IReadOnlyDictionary<uint, string> strings,
        string expected)
    {
        return TryReadUInt32(body, ref position, out var token) &&
               TryResolveName(token, strings, out var actual) &&
               string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private static bool TryReadReflectedString(
        ReadOnlySpan<byte> body,
        ref int position,
        out string value)
    {
        value = string.Empty;
        if (!TryReadUInt16(body, ref position, out var byteCount) ||
            byteCount == 0 ||
            position > body.Length - byteCount)
        {
            return false;
        }

        var bytes = body.Slice(position, byteCount);
        position += byteCount;
        if (bytes[^1] != 0 || bytes[..^1].Contains((byte)0) || ContainsNonAscii(bytes[..^1]))
        {
            return false;
        }

        value = Encoding.ASCII.GetString(bytes[..^1]);
        return true;
    }

    private static bool TryReadFiniteFloat(
        ReadOnlySpan<byte> body,
        ref int position,
        out float value)
    {
        value = 0;
        if (!TryReadUInt32(body, ref position, out var bits))
        {
            return false;
        }

        value = BitConverter.UInt32BitsToSingle(bits);
        return float.IsFinite(value);
    }

    private static bool TryReadUInt16(
        ReadOnlySpan<byte> body,
        ref int position,
        out ushort value)
    {
        value = 0;
        if (position < 0 || position > body.Length - sizeof(ushort))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(body[position..]);
        position += sizeof(ushort);
        return true;
    }

    private static bool TryReadUInt32(
        ReadOnlySpan<byte> body,
        ref int position,
        out uint value)
    {
        value = 0;
        if (position < 0 || position > body.Length - sizeof(uint))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(body[position..]);
        position += sizeof(uint);
        return true;
    }

    private static bool TryResolveName(
        uint token,
        IReadOnlyDictionary<uint, string> strings,
        out string value) => strings.TryGetValue(token, out value!);

    private static bool ContainsNonAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value > 0x7F)
            {
                return true;
            }
        }

        return false;
    }

    private static string AxisName(int axis) => axis switch
    {
        0 => "X",
        1 => "Y",
        2 => "Z",
        _ => "unknown"
    };

    private static string ChunkName(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return ContainsNonAscii(bytes)
            ? $"0x{value:X8}"
            : $"'{Encoding.ASCII.GetString(bytes)}'";
    }

    private static ExpectedField NamedField(string name, string type, uint runtimeOffset) =>
        new(name, type, null, runtimeOffset);

    private static ExpectedField BuiltInField(string name, uint type, uint runtimeOffset) =>
        new(name, null, type, runtimeOffset);

    private sealed record ExpectedClass(
        string Name,
        ushort Flags,
        IReadOnlyList<ExpectedField> Fields);

    private sealed record ExpectedField(
        string Name,
        string? NamedType,
        uint? BuiltInType,
        uint RuntimeOffset);

    private readonly record struct CurveMetadata(
        float MaxInput,
        float MinInput,
        float InputDistance,
        float MaxValue,
        float MinValue,
        float DefaultValue,
        string CurveType,
        string EdgeMode,
        bool IsSampleInterpolating,
        uint SerializedControlListMarker);
}
