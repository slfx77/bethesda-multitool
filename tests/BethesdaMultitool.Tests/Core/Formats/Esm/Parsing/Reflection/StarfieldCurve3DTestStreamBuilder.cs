using System.Text;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection;

// public: consumed as a [Theory] parameter, which must be at least as accessible as the
// public test method xUnit requires.
public enum StarfieldCurve3DSchemaMutation
{
    None,
    WrongClassOrder,
    WrongFormToken,
    WrongFloatCurveFlags,
    WrongFieldOrder,
    DuplicateField,
    WrongFieldType,
    WrongRuntimeOffset
}

public enum StarfieldCurve3DLayoutMutation
{
    None,
    DiffObject,
    ReorderedUserAndList,
    UnknownSideChunk,
    DuplicateFinalChunk,
    WrongUserType,
    WrongListElementType,
    ImpossibleControlCount,
    MalformedMetadataString,
    InvalidInterpolationBool,
    NonFiniteMetadata,
    NonFiniteControl,
    TruncatedStream,
    TrailingByte
}

/// <summary>Purpose-built exact-schema BETH-v4 CUR3 fixtures; no shared helper is modified.</summary>
internal static class StarfieldCurve3DTestStreamBuilder
{
    private const string RootType = "BGSCurve3DForm";
    private const string Curve3DType = "BSFloat3DCurve";
    private const string FloatCurveType = "BSFloatCurve";
    private const string ControlType = "BSFloatCurve::Control";

    private const uint TypeString = 0xFFFFFF02;
    private const uint TypeList = 0xFFFFFF03;
    private const uint TypeBool = 0xFFFFFF10;
    private const uint TypeFloat = 0xFFFFFF11;

    internal static byte[] Build(
        uint serializedControlListMarker = 1,
        StarfieldCurve3DSchemaMutation schemaMutation = StarfieldCurve3DSchemaMutation.None,
        StarfieldCurve3DLayoutMutation layoutMutation = StarfieldCurve3DLayoutMutation.None)
    {
        var schema = BuildSchema(schemaMutation);
        var axes = new[]
        {
            new Axis(
                1f,
                -2f,
                3f,
                100f,
                -25f,
                0.5f,
                [new Control(-2f, 100f), new Control(0f, 0f), new Control(1f, -25f)]),
            new Axis(
                10f,
                0f,
                0.25f,
                40f,
                10f,
                20f,
                [
                    new Control(0f, 10f),
                    new Control(0.5f, 20f),
                    new Control(1f, 30f),
                    new Control(2f, 40f)
                ]),
            new Axis(
                2f,
                0f,
                2f,
                4f,
                1f,
                2f,
                [new Control(0f, 1f), new Control(2f, 4f)])
        };

        if (layoutMutation == StarfieldCurve3DLayoutMutation.NonFiniteMetadata)
        {
            axes[0] = axes[0] with { MaxValue = float.NaN };
        }
        else if (layoutMutation == StarfieldCurve3DLayoutMutation.NonFiniteControl)
        {
            axes[0] = axes[0] with
            {
                Controls = [new Control(0f, float.PositiveInfinity)]
            };
        }

        var chunks = new List<ChunkData>(schema.Chunks)
        {
            new(
                layoutMutation == StarfieldCurve3DLayoutMutation.DiffObject ? "DIFF" : "OBJT",
                U32(schema.Tokens[RootType]))
        };

        for (var axis = 0; axis < axes.Length; axis++)
        {
            var malformedString = axis == 0 &&
                                  layoutMutation ==
                                  StarfieldCurve3DLayoutMutation.MalformedMetadataString;
            var invalidBool = axis == 0 &&
                              layoutMutation ==
                              StarfieldCurve3DLayoutMutation.InvalidInterpolationBool;
            var wrongUserType = axis == 0 &&
                                layoutMutation == StarfieldCurve3DLayoutMutation.WrongUserType;
            var wrongListType = axis == 0 &&
                                layoutMutation ==
                                StarfieldCurve3DLayoutMutation.WrongListElementType;
            var impossibleCount = axis == 0 &&
                                  layoutMutation ==
                                  StarfieldCurve3DLayoutMutation.ImpossibleControlCount;

            chunks.Add(new ChunkData(
                "USER",
                BuildUser(
                    schema.Tokens,
                    axes[axis],
                    serializedControlListMarker,
                    malformedString ? "Cubic\0Spline" : "CubicSpline",
                    "Clamp",
                    invalidBool ? (byte)2 : (byte)1,
                    wrongUserType ? Curve3DType : FloatCurveType)));
            chunks.Add(new ChunkData(
                "LIST",
                BuildList(
                    schema.Tokens,
                    axes[axis].Controls,
                    wrongListType ? FloatCurveType : ControlType,
                    impossibleCount ? uint.MaxValue : null)));
        }

        switch (layoutMutation)
        {
            case StarfieldCurve3DLayoutMutation.ReorderedUserAndList:
                (chunks[6], chunks[7]) = (chunks[7], chunks[6]);
                break;
            case StarfieldCurve3DLayoutMutation.UnknownSideChunk:
                chunks[6] = chunks[6] with { Signature = "NOPE" };
                break;
            case StarfieldCurve3DLayoutMutation.DuplicateFinalChunk:
                chunks.Add(chunks[^1]);
                break;
        }

        var stream = ReflectionStream(schema.StringTable, chunks);
        return layoutMutation switch
        {
            StarfieldCurve3DLayoutMutation.TruncatedStream => stream[..^1],
            StarfieldCurve3DLayoutMutation.TrailingByte => [.. stream, 0xCC],
            _ => stream
        };
    }

    private static Schema BuildSchema(StarfieldCurve3DSchemaMutation mutation)
    {
        string[] names =
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

        var tokens = new Dictionary<string, uint>(StringComparer.Ordinal);
        var stringTable = new List<byte>();
        foreach (var name in names)
        {
            tokens.Add(name, checked((uint)stringTable.Count));
            stringTable.AddRange(Encoding.ASCII.GetBytes(name));
            stringTable.Add(0);
        }

        uint Named(string name) => tokens[name];
        var curve3DFields = new[]
        {
            new Field("XCurve", Named(FloatCurveType), 4_194_304),
            new Field("YCurve", Named(FloatCurveType), 4_194_368),
            new Field("ZCurve", Named(FloatCurveType), 4_194_432)
        };
        if (mutation == StarfieldCurve3DSchemaMutation.WrongFieldOrder)
        {
            (curve3DFields[0], curve3DFields[1]) = (curve3DFields[1], curve3DFields[0]);
        }
        else if (mutation == StarfieldCurve3DSchemaMutation.DuplicateField)
        {
            curve3DFields[1] = curve3DFields[1] with { Name = "XCurve" };
        }

        var floatCurveFields = new[]
        {
            new Field("Controls", TypeList, 1_572_872),
            new Field(
                "MaxInput",
                mutation == StarfieldCurve3DSchemaMutation.WrongFieldType
                    ? TypeString
                    : TypeFloat,
                262_176),
            new Field("MinInput", TypeFloat, 262_180),
            new Field("InputDistance", TypeFloat, 262_184),
            new Field("MaxValue", TypeFloat, 262_188),
            new Field("MinValue", TypeFloat, 262_192),
            new Field("DefaultValue", TypeFloat, 262_196),
            new Field("Type", TypeString, 65_592),
            new Field("Edge", TypeString, 65_593),
            new Field("IsSampleInterpolating", TypeBool, 65_594)
        };
        if (mutation == StarfieldCurve3DSchemaMutation.WrongRuntimeOffset)
        {
            floatCurveFields[0] = floatCurveFields[0] with { RuntimeOffset = 1_572_873 };
        }

        var formToken = mutation == StarfieldCurve3DSchemaMutation.WrongFormToken
            ? Named(Curve3DType)
            : Named(RootType);
        var classChunks = new List<ChunkData>
        {
            ClassChunk(tokens, Curve3DType, formToken, 0, curve3DFields),
            ClassChunk(
                tokens,
                FloatCurveType,
                formToken,
                mutation == StarfieldCurve3DSchemaMutation.WrongFloatCurveFlags
                    ? (ushort)0
                    : (ushort)0x000C,
                floatCurveFields),
            ClassChunk(
                tokens,
                RootType,
                formToken,
                0,
                [new Field("Curve", Named(Curve3DType), 12_583_192)]),
            ClassChunk(
                tokens,
                ControlType,
                formToken,
                0,
                [
                    new Field("Input", TypeFloat, 262_144),
                    new Field("Value", TypeFloat, 262_148)
                ])
        };
        if (mutation == StarfieldCurve3DSchemaMutation.WrongClassOrder)
        {
            (classChunks[0], classChunks[1]) = (classChunks[1], classChunks[0]);
        }

        var chunks = new List<ChunkData> { new("TYPE", U32(4)) };
        chunks.AddRange(classChunks);
        return new Schema(tokens, [.. stringTable], chunks);
    }

    private static ChunkData ClassChunk(
        IReadOnlyDictionary<string, uint> tokens,
        string className,
        uint formToken,
        ushort flags,
        IReadOnlyList<Field> fields)
    {
        var body = new List<byte>();
        body.AddRange(U32(tokens[className]));
        body.AddRange(U32(formToken));
        body.AddRange(U16(flags));
        body.AddRange(U16(checked((ushort)fields.Count)));
        foreach (var field in fields)
        {
            body.AddRange(U32(tokens[field.Name]));
            body.AddRange(U32(field.Type));
            body.AddRange(U32(field.RuntimeOffset));
        }

        return new ChunkData("CLAS", [.. body]);
    }

    private static byte[] BuildUser(
        IReadOnlyDictionary<string, uint> tokens,
        Axis axis,
        uint marker,
        string curveType,
        string edgeMode,
        byte interpolation,
        string serializedType)
    {
        var body = new List<byte>();
        body.AddRange(U32(tokens[FloatCurveType]));
        body.AddRange(U32(tokens[serializedType]));
        body.AddRange(F32(axis.MaxInput));
        body.AddRange(F32(axis.MinInput));
        body.AddRange(F32(axis.InputDistance));
        body.AddRange(F32(axis.MaxValue));
        body.AddRange(F32(axis.MinValue));
        body.AddRange(F32(axis.DefaultValue));
        body.AddRange(ReflectedString(curveType));
        body.AddRange(ReflectedString(edgeMode));
        body.Add(interpolation);
        body.AddRange(U32(marker));
        return [.. body];
    }

    private static byte[] BuildList(
        IReadOnlyDictionary<string, uint> tokens,
        IReadOnlyList<Control> controls,
        string elementType,
        uint? countOverride)
    {
        var body = new List<byte>();
        body.AddRange(U32(tokens[elementType]));
        body.AddRange(U32(countOverride ?? checked((uint)controls.Count)));
        foreach (var control in controls)
        {
            body.AddRange(F32(control.Input));
            body.AddRange(F32(control.Value));
        }

        return [.. body];
    }

    private static byte[] ReflectionStream(byte[] strings, IReadOnlyList<ChunkData> chunks)
    {
        var parts = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("BETH"),
            U32(8),
            U32(4),
            U32(checked((uint)chunks.Count + 2)),
            Encoding.ASCII.GetBytes("STRT"),
            U32(checked((uint)strings.Length)),
            strings
        };
        parts.AddRange(chunks.Select(chunk => Chunk(chunk.Signature, chunk.Body)));
        return Concat([.. parts]);
    }

    private static byte[] ReflectedString(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        return Concat(U16(checked((ushort)(bytes.Length + 1))), bytes, [0]);
    }

    private static byte[] Chunk(string signature, byte[] body) =>
        Concat(Encoding.ASCII.GetBytes(signature), U32(checked((uint)body.Length)), body);

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);
    private static byte[] U16(ushort value) => BitConverter.GetBytes(value);
    private static byte[] F32(float value) => BitConverter.GetBytes(value);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new List<byte>();
        foreach (var part in parts)
        {
            result.AddRange(part);
        }

        return [.. result];
    }

    private sealed record Schema(
        IReadOnlyDictionary<string, uint> Tokens,
        byte[] StringTable,
        IReadOnlyList<ChunkData> Chunks);

    private sealed record ChunkData(string Signature, byte[] Body);
    private sealed record Field(string Name, uint Type, uint RuntimeOffset);

    private sealed record Axis(
        float MaxInput,
        float MinInput,
        float InputDistance,
        float MaxValue,
        float MinValue,
        float DefaultValue,
        IReadOnlyList<Control> Controls);

    private sealed record Control(float Input, float Value);
}
