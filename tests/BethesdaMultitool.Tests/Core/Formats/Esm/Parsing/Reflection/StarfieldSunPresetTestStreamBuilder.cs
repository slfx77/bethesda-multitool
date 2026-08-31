using System.Text;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection;

// public: consumed as a [Theory] parameter, which must be at least as accessible as the
// public test method xUnit requires.
public enum StarfieldSunPresetSchemaMutation
{
    None,
    WrongClassOrder,
    WrongFormToken,
    WrongFloat4Flags,
    WrongRootFieldOrder,
    WrongRootFieldType,
    WrongRuntimeOffset
}

/// <summary>Purpose-built exact-schema BETH-v4 SUNP fixtures; no shared test helper is modified.</summary>
internal static class StarfieldSunPresetTestStreamBuilder
{
    private const string RootType = "BSGalaxy::BGSSunPresetForm";
    private const string NightType = "BSGalaxy::BGSSunPresetForm::NightSettings";
    private const string DawnDuskType = "BSGalaxy::BGSSunPresetForm::DawnDuskSettings";
    private const string Float4Type = "XMFLOAT4";

    internal const uint TypeString = 0xFFFFFF02;
    internal const uint TypeRef = 0xFFFFFF05;
    internal const uint TypeUInt32 = 0xFFFFFF0D;
    internal const uint TypeFloat = 0xFFFFFF11;

    internal static byte[] BuildFull(
        uint reflectedParent = 0,
        string diskTexture = "",
        float sunIlluminance = 20_000f,
        uint referenceValueType = TypeUInt32,
        string objectChunk = "OBJT",
        bool truncateObject = false,
        bool appendTrailingByte = false,
        StarfieldSunPresetSchemaMutation schemaMutation = StarfieldSunPresetSchemaMutation.None)
    {
        var schema = BuildSchema(schemaMutation);
        var body = new List<byte>();
        body.AddRange(U32(schema.Tokens[RootType]));
        body.AddRange(U32(referenceValueType));
        body.AddRange(referenceValueType == TypeFloat ? F32(reflectedParent) : U32(reflectedParent));
        AddFloat4(body, 0f, 1f, 2f, 3f);
        body.AddRange(F32(sunIlluminance));
        AddFloat4(body, 4f, 5f, 6f, 1f);
        body.AddRange(ReflectedString(diskTexture));
        body.AddRange(F32(0f));
        body.AddRange(F32(0.138f));
        AddFloat4(body, 7f, 8f, 9f, 1f);
        body.AddRange(F32(50f));
        body.AddRange(F32(80f));
        AddFloat4(body, 10f, 11f, 12f, 1f);
        body.AddRange(F32(100f));
        AddFloat4(body, 0f, 0f, 0f, 1f);

        if (truncateObject)
        {
            body.RemoveAt(body.Count - 1);
        }

        var chunks = new List<byte[]>(schema.Chunks) { Chunk(objectChunk, [.. body]) };
        var stream = ReflectionStream(schema.StringTable, chunks);
        return appendTrailingByte ? [.. stream, 0xCC] : stream;
    }

    internal static byte[] BuildDiff(
        uint reflectedParent = 0x000E66B6,
        bool omitReflectedParent = false,
        bool includeSunColorX = false,
        float sunColorX = 0f,
        string? diskTexture = null,
        bool includeDawnColor = false,
        bool includeNightColor = false,
        uint referenceValueType = TypeUInt32,
        string objectChunk = "DIFF",
        bool duplicateParentField = false,
        bool appendOutOfRangeFieldIndex = false,
        bool omitRootTerminator = false,
        StarfieldSunPresetSchemaMutation schemaMutation = StarfieldSunPresetSchemaMutation.None)
    {
        var schema = BuildSchema(schemaMutation);
        var body = new List<byte> { };
        body.AddRange(U32(schema.Tokens[RootType]));

        if (!omitReflectedParent)
        {
            body.AddRange(U16(0));
            body.AddRange(U32(referenceValueType));
            body.AddRange(referenceValueType == TypeFloat
                ? F32(reflectedParent)
                : U32(reflectedParent));
        }

        if (includeSunColorX)
        {
            body.AddRange(U16(1));
            body.AddRange(U16(0));
            body.AddRange(F32(sunColorX));
            body.AddRange(U16(ushort.MaxValue));
        }

        if (diskTexture is not null)
        {
            body.AddRange(U16(4));
            body.AddRange(ReflectedString(diskTexture));
        }

        if (includeDawnColor)
        {
            body.AddRange(U16(7));
            body.AddRange(U16(0));
            AddDiffFloat4(body, 0.25f, 0.5f, 0.75f, 1f);
            body.AddRange(U16(ushort.MaxValue));
        }

        if (includeNightColor)
        {
            body.AddRange(U16(8));
            body.AddRange(U16(0));
            AddDiffFloat4(body, 0f, 0.125f, 0.25f, 1f);
            body.AddRange(U16(ushort.MaxValue));
        }

        if (duplicateParentField)
        {
            body.AddRange(U16(0));
            body.AddRange(U32(TypeUInt32));
            body.AddRange(U32(reflectedParent));
        }

        if (appendOutOfRangeFieldIndex)
        {
            body.AddRange(U16(9));
        }

        if (!omitRootTerminator)
        {
            body.AddRange(U16(ushort.MaxValue));
        }
        var chunks = new List<byte[]>(schema.Chunks) { Chunk(objectChunk, [.. body]) };
        return ReflectionStream(schema.StringTable, chunks);
    }

    private static Schema BuildSchema(StarfieldSunPresetSchemaMutation mutation)
    {
        string[] names =
        [
            NightType,
            RootType,
            Float4Type,
            DawnDuskType,
            "DirectionalColor",
            "DirectionalIlluminance",
            "GlareColor",
            "pParent",
            "SunColor",
            "SunIlluminance",
            "SunGlareColor",
            "SunDiskTexture",
            "SunDiskScreenSizeMin",
            "SunDiskScreenSizeMax",
            "DuskDawnPreset",
            "NightPreset",
            "x",
            "y",
            "z",
            "w",
            "TransitionStartAngle",
            "TransitionEndAngle"
        ];

        var tokens = new Dictionary<string, uint>(StringComparer.Ordinal);
        var stringTable = new List<byte>();
        foreach (var name in names)
        {
            if (tokens.ContainsKey(name)) continue;
            tokens.Add(name, checked((uint)stringTable.Count));
            stringTable.AddRange(Encoding.ASCII.GetBytes(name));
            stringTable.Add(0);
        }

        uint Named(string name) => tokens[name];

        var nightFields = new[]
        {
            new Field("DirectionalColor", Named(Float4Type), 0),
            new Field("DirectionalIlluminance", TypeFloat, 16),
            new Field("GlareColor", Named(Float4Type), 20)
        };
        var rootFields = new[]
        {
            new Field("pParent", TypeRef, 280),
            new Field("SunColor", Named(Float4Type), 288),
            new Field(
                "SunIlluminance",
                mutation == StarfieldSunPresetSchemaMutation.WrongRootFieldType
                    ? TypeUInt32
                    : TypeFloat,
                304),
            new Field("SunGlareColor", Named(Float4Type), 308),
            new Field("SunDiskTexture", TypeString, 328),
            new Field("SunDiskScreenSizeMin", TypeFloat, 336),
            new Field("SunDiskScreenSizeMax", TypeFloat, 340),
            new Field("DuskDawnPreset", Named(DawnDuskType), 344),
            new Field("NightPreset", Named(NightType), 368)
        };
        if (mutation == StarfieldSunPresetSchemaMutation.WrongRootFieldOrder)
        {
            (rootFields[1], rootFields[2]) = (rootFields[2], rootFields[1]);
        }
        if (mutation == StarfieldSunPresetSchemaMutation.WrongRuntimeOffset)
        {
            rootFields[1] = rootFields[1] with { RuntimeOffset = 289 };
        }

        var float4Fields = new[]
        {
            new Field("x", TypeFloat, 0),
            new Field("y", TypeFloat, 4),
            new Field("z", TypeFloat, 8),
            new Field("w", TypeFloat, 12)
        };
        var dawnFields = new[]
        {
            new Field("DirectionalColor", Named(Float4Type), 0),
            new Field("TransitionStartAngle", TypeFloat, 16),
            new Field("TransitionEndAngle", TypeFloat, 20)
        };

        var formToken = mutation == StarfieldSunPresetSchemaMutation.WrongFormToken
            ? Named(Float4Type)
            : Named(RootType);
        var classChunks = new List<byte[]>
        {
            ClassChunk(tokens, NightType, formToken, 0, nightFields),
            ClassChunk(tokens, RootType, Named(RootType), 0, rootFields),
            ClassChunk(
                tokens,
                Float4Type,
                Named(RootType),
                mutation == StarfieldSunPresetSchemaMutation.WrongFloat4Flags
                    ? (ushort)0
                    : (ushort)8,
                float4Fields),
            ClassChunk(tokens, DawnDuskType, Named(RootType), 0, dawnFields)
        };
        if (mutation == StarfieldSunPresetSchemaMutation.WrongClassOrder)
        {
            (classChunks[0], classChunks[1]) = (classChunks[1], classChunks[0]);
        }

        var chunks = new List<byte[]> { Chunk("TYPE", U32(4)) };
        chunks.AddRange(classChunks);
        return new Schema(tokens, [.. stringTable], chunks);
    }

    private static byte[] ClassChunk(
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

        return Chunk("CLAS", [.. body]);
    }

    private static byte[] ReflectionStream(byte[] strings, IReadOnlyList<byte[]> chunks) =>
        Concat(
            Encoding.ASCII.GetBytes("BETH"),
            U32(8),
            U32(4),
            U32(checked((uint)chunks.Count + 2)),
            Encoding.ASCII.GetBytes("STRT"),
            U32(checked((uint)strings.Length)),
            strings,
            Concat([.. chunks]));

    private static byte[] ReflectedString(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        return Concat(U16(checked((ushort)(bytes.Length + 1))), bytes, [0]);
    }

    private static void AddFloat4(List<byte> body, float x, float y, float z, float w)
    {
        body.AddRange(F32(x));
        body.AddRange(F32(y));
        body.AddRange(F32(z));
        body.AddRange(F32(w));
    }

    private static void AddDiffFloat4(List<byte> body, float x, float y, float z, float w)
    {
        var values = new[] { x, y, z, w };
        for (ushort index = 0; index < values.Length; index++)
        {
            body.AddRange(U16(index));
            body.AddRange(F32(values[index]));
        }

        body.AddRange(U16(ushort.MaxValue));
    }

    private static byte[] Chunk(string signature, byte[] body) =>
        Concat(Encoding.ASCII.GetBytes(signature), U32(checked((uint)body.Length)), body);

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);
    private static byte[] U16(ushort value) => BitConverter.GetBytes(value);
    private static byte[] F32(float value) => BitConverter.GetBytes(value);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new List<byte>();
        foreach (var part in parts) result.AddRange(part);
        return [.. result];
    }

    private sealed record Schema(
        IReadOnlyDictionary<string, uint> Tokens,
        byte[] StringTable,
        IReadOnlyList<byte[]> Chunks);

    private sealed record Field(string Name, uint Type, uint RuntimeOffset);
}
