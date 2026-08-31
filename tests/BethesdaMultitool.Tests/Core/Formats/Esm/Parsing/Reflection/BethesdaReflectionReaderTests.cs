using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Reflection;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection;

public sealed class BethesdaReflectionReaderTests
{
    private const uint TypeList = 0xFFFFFF03;
    private const uint TypeRef = 0xFFFFFF05;
    private const uint TypeUInt32 = 0xFFFFFF0D;
    private const uint TypeUInt64 = 0xFFFFFF0F;
    private const uint TypeFloat = 0xFFFFFF11;

    [Fact]
    public void ReadObject_DecodesSequentialClassesReferencesAndOutOfLineLists()
    {
        var stream = BuildFullStream();

        var ok = BethesdaReflectionReader.TryReadObject(
            stream, false, "Root", out var root, out var error);

        Assert.True(ok, error);
        Assert.NotNull(root);
        var parent = Assert.IsType<BethesdaReflectionReferenceValue>(root.Fields["Parent"]);
        Assert.Equal("UInt32", parent.ValueType);
        Assert.Equal(0x01020304ul, Assert.IsType<BethesdaReflectionUnsignedValue>(parent.Value).Value);

        var nested = Assert.IsType<BethesdaReflectionObjectValue>(root.Fields["Nested"]).Value;
        Assert.Equal(7ul, Assert.IsType<BethesdaReflectionUnsignedValue>(nested.Fields["Weight"]).Value);
        var vector = Assert.IsType<BethesdaReflectionObjectValue>(nested.Fields["Vector"]).Value;
        Assert.Equal(0.25, Assert.IsType<BethesdaReflectionFloatValue>(vector.Fields["x"]).Value, 6);
        Assert.Equal(0.5, Assert.IsType<BethesdaReflectionFloatValue>(vector.Fields["y"]).Value, 6);
        Assert.Equal(0.75, Assert.IsType<BethesdaReflectionFloatValue>(vector.Fields["z"]).Value, 6);
        Assert.Equal(1.0, Assert.IsType<BethesdaReflectionFloatValue>(vector.Fields["w"]).Value, 6);

        var list = Assert.IsType<BethesdaReflectionListValue>(root.Fields["Items"]);
        Assert.Equal("UInt32", list.ElementType);
        Assert.Equal(
            [11ul, 22ul],
            list.Values.Select(item => Assert.IsType<BethesdaReflectionUnsignedValue>(item).Value));
    }

    [Fact]
    public void Diff_MergesOnlyIndexedNestedComponents()
    {
        Assert.True(BethesdaReflectionReader.TryReadObject(
            BuildFullStream(), false, "Root", out var inherited, out var fullError), fullError);
        Assert.True(BethesdaReflectionReader.TryReadObject(
            BuildDiffStream(), true, "Root", out var overlay, out var diffError), diffError);

        var ok = BethesdaReflectionReader.TryMerge(inherited!, overlay!, out var merged);

        Assert.True(ok);
        Assert.NotNull(merged);
        var parent = Assert.IsType<BethesdaReflectionReferenceValue>(merged.Fields["Parent"]);
        Assert.Equal(0xAABBCCDDul, Assert.IsType<BethesdaReflectionUnsignedValue>(parent.Value).Value);
        var nested = Assert.IsType<BethesdaReflectionObjectValue>(merged.Fields["Nested"]).Value;
        Assert.Equal(7ul, Assert.IsType<BethesdaReflectionUnsignedValue>(nested.Fields["Weight"]).Value);
        var vector = Assert.IsType<BethesdaReflectionObjectValue>(nested.Fields["Vector"]).Value;
        Assert.Equal(0.25, Assert.IsType<BethesdaReflectionFloatValue>(vector.Fields["x"]).Value, 6);
        Assert.Equal(0.5, Assert.IsType<BethesdaReflectionFloatValue>(vector.Fields["y"]).Value, 6);
        Assert.Equal(0.125, Assert.IsType<BethesdaReflectionFloatValue>(vector.Fields["z"]).Value, 6);
        Assert.Equal(1.0, Assert.IsType<BethesdaReflectionFloatValue>(vector.Fields["w"]).Value, 6);
        Assert.Equal(
            [11ul, 22ul],
            Assert.IsType<BethesdaReflectionListValue>(merged.Fields["Items"]).Values
                .Select(item => Assert.IsType<BethesdaReflectionUnsignedValue>(item).Value));
    }

    [Fact]
    public void ReadObject_RejectsTruncatedNestedDiffTerminator()
    {
        var stream = BuildDiffStream()[..^2];

        Assert.False(BethesdaReflectionReader.TryReadObject(
            stream, true, "Root", out var value, out var error));
        Assert.Null(value);
        Assert.NotNull(error);
    }

    [Fact]
    public void ReadObject_RejectsMissingOutOfLineListChunk()
    {
        var stream = BuildFullStream(includeList: false);

        Assert.False(BethesdaReflectionReader.TryReadObject(
            stream, false, "Root", out var value, out var error));
        Assert.Null(value);
        Assert.Contains("LIST", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadObject_RejectsBytesOutsideDeclaredChunks()
    {
        var stream = BuildFullStream().Concat(new byte[] { 0xCC }).ToArray();

        Assert.False(BethesdaReflectionReader.TryReadObject(
            stream, false, "Root", out var value, out var error));
        Assert.Null(value);
        Assert.Contains("trailing", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadObject_RejectsTypeCountThatDoesNotMatchClassDefinitions()
    {
        var schema = BuildSchema();
        var stream = BuildFullStream();
        var typeCountOffset = 24 + schema.StringTable.Length + 8;
        BitConverter.GetBytes(99u).CopyTo(stream, typeCountOffset);

        Assert.False(BethesdaReflectionReader.TryReadObject(
            stream, false, "Root", out var value, out var error));
        Assert.Null(value);
        Assert.Contains("TYPE class count", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadObject_PreservesValidatedUserConversionPayload(bool isDiff)
    {
        var payload = Concat(U64(0xE479117A3B3E42AB), U32(0xA0D3B014));

        Assert.True(BethesdaReflectionReader.TryReadObject(
            BuildUserStream(isDiff, payload), isDiff, "Root", out var root, out var error), error);

        var user = Assert.IsType<BethesdaReflectionUserValue>(root!.Fields["Hook"]);
        Assert.Equal("Hook", user.DeclaredType);
        Assert.Equal("UInt64", user.SerializedType);
        Assert.Equal(isDiff, user.IsDiff);
        Assert.Equal(payload, user.SerializedPayload);
    }

    [Fact]
    public void ReadObject_RejectsUserChunkWithWrongDeclaredClass()
    {
        Assert.False(BethesdaReflectionReader.TryReadObject(
            BuildUserStream(false, U64(1), wrongDeclaredType: true),
            false, "Root", out var value, out var error));

        Assert.Null(value);
        Assert.Contains("expected class 'Hook'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadObject_RejectsMissingUserSideChunk()
    {
        Assert.False(BethesdaReflectionReader.TryReadObject(
            BuildUserStream(false, U64(1), includeUserChunk: false),
            false, "Root", out var value, out var error));

        Assert.Null(value);
        Assert.Contains("USER/USRD", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xFFFFFF06u)]
    [InlineData(0xFFFFFF07u)]
    public void ReadObject_RejectsReservedUserSerializedTypeToken(uint serializedType)
    {
        Assert.False(BethesdaReflectionReader.TryReadObject(
            BuildUserStream(false, U64(1), serializedType: serializedType),
            false, "Root", out var value, out var error));

        Assert.Null(value);
        Assert.Contains("unsupported serialized type", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadObject_UsesUserSideChunkModeIndependentlyOfObject(bool objectIsDiff)
    {
        Assert.True(BethesdaReflectionReader.TryReadObject(
            BuildUserStream(
                objectIsDiff,
                Concat(U64(1), U32(TypeUInt64)),
                userChunkIsDiff: !objectIsDiff),
            objectIsDiff, "Root", out var root, out var error), error);

        var user = Assert.IsType<BethesdaReflectionUserValue>(root!.Fields["Hook"]);
        Assert.Equal(!objectIsDiff, user.IsDiff);
    }

    private static byte[] BuildFullStream(bool includeList = true)
    {
        var schema = BuildSchema();
        var root = Chunk("OBJT", Concat(
            U32(schema.Offsets["Root"]),
            U32(TypeUInt32), U32(0x01020304),
            U32(7),
            F32(0.25f), F32(0.5f), F32(0.75f), F32(1f)));
        var chunks = new List<byte[]>(schema.ClassChunks) { root };
        if (includeList)
        {
            chunks.Add(Chunk("LIST", Concat(U32(TypeUInt32), U32(2), U32(11), U32(22))));
        }

        return ReflectionStream(schema.StringTable, chunks);
    }

    private static byte[] BuildDiffStream()
    {
        var schema = BuildSchema();
        var diff = Chunk("DIFF", Concat(
            U32(schema.Offsets["Root"]),
            U16(0), U32(TypeUInt32), U32(0xAABBCCDD),
            U16(1),
            U16(1),
            U16(2), F32(0.125f), U16(ushort.MaxValue),
            U16(ushort.MaxValue),
            U16(ushort.MaxValue)));
        var chunks = new List<byte[]>(schema.ClassChunks) { diff };
        return ReflectionStream(schema.StringTable, chunks);
    }

    private static byte[] BuildUserStream(
        bool isDiff,
        byte[] serializedPayload,
        bool wrongDeclaredType = false,
        bool includeUserChunk = true,
        bool? userChunkIsDiff = null,
        uint serializedType = TypeUInt64)
    {
        string[] names = ["Root", "Hook", "HighGUID", "Other"];
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        var strings = new List<byte>();
        foreach (var name in names)
        {
            offsets[name] = (uint)strings.Count;
            strings.AddRange(Encoding.ASCII.GetBytes(name));
            strings.Add(0);
        }

        var objectBody = isDiff
            ? Concat(U32(offsets["Root"]), U16(0), U16(ushort.MaxValue))
            : U32(offsets["Root"]);
        var chunks = new List<byte[]>
        {
            Chunk("TYPE", U32(2)),
            ClassChunkWithFlags(offsets, "Hook", 4, ("HighGUID", TypeUInt64)),
            ClassChunk(offsets, "Root", ("Hook", offsets["Hook"])),
            Chunk(isDiff ? "DIFF" : "OBJT", objectBody)
        };
        if (includeUserChunk)
        {
            chunks.Add(Chunk((userChunkIsDiff ?? isDiff) ? "USRD" : "USER", Concat(
                U32(offsets[wrongDeclaredType ? "Other" : "Hook"]),
                U32(serializedType),
                serializedPayload)));
        }

        return ReflectionStream([.. strings], chunks);
    }

    private static ReflectionSchema BuildSchema()
    {
        string[] names = ["Root", "Parent", "Nested", "Items", "Weight", "Vector", "XMFLOAT4", "x", "y", "z", "w"];
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        var strings = new List<byte>();
        foreach (var name in names)
        {
            offsets[name] = (uint)strings.Count;
            strings.AddRange(Encoding.ASCII.GetBytes(name));
            strings.Add(0);
        }

        var chunks = new List<byte[]>
        {
            Chunk("TYPE", U32(3)),
            ClassChunk(offsets, "XMFLOAT4",
                ("x", TypeFloat), ("y", TypeFloat), ("z", TypeFloat), ("w", TypeFloat)),
            ClassChunk(offsets, "Nested",
                ("Weight", TypeUInt32), ("Vector", offsets["XMFLOAT4"])),
            ClassChunk(offsets, "Root",
                ("Parent", TypeRef), ("Nested", offsets["Nested"]), ("Items", TypeList))
        };
        return new ReflectionSchema(offsets, [.. strings], chunks);
    }

    private static byte[] ClassChunk(
        IReadOnlyDictionary<string, uint> offsets,
        string className,
        params (string Name, uint Type)[] fields)
    {
        return ClassChunkWithFlags(offsets, className, 0, fields);
    }

    private static byte[] ClassChunkWithFlags(
        IReadOnlyDictionary<string, uint> offsets,
        string className,
        ushort flags,
        params (string Name, uint Type)[] fields)
    {
        var body = new List<byte>();
        body.AddRange(U32(offsets[className]));
        body.AddRange(U32(0));
        body.AddRange(U16(flags));
        body.AddRange(U16(checked((ushort)fields.Length)));
        foreach (var field in fields)
        {
            body.AddRange(U32(offsets[field.Name]));
            body.AddRange(U32(field.Type));
            body.AddRange(U16(0));
            body.AddRange(U16(0));
        }

        return Chunk("CLAS", [.. body]);
    }

    private static byte[] ReflectionStream(byte[] strings, IReadOnlyList<byte[]> chunks)
    {
        return Concat(
            Encoding.ASCII.GetBytes("BETH"), U32(8), U32(4), U32((uint)chunks.Count + 2),
            Encoding.ASCII.GetBytes("STRT"), U32((uint)strings.Length), strings,
            Concat([.. chunks]));
    }

    private static byte[] Chunk(string signature, byte[] body)
    {
        return Concat(Encoding.ASCII.GetBytes(signature), U32((uint)body.Length), body);
    }

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);

    private static byte[] U64(ulong value) => BitConverter.GetBytes(value);

    private static byte[] U16(ushort value) => BitConverter.GetBytes(value);

    private static byte[] F32(float value) => BitConverter.GetBytes(value);

    private static byte[] Concat(params byte[][] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts) bytes.AddRange(part);
        return [.. bytes];
    }

    private sealed record ReflectionSchema(
        IReadOnlyDictionary<string, uint> Offsets,
        byte[] StringTable,
        IReadOnlyList<byte[]> ClassChunks);
}
