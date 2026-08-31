using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class StarfieldAtmosphereParsingTests
{
    private const string RootType = "BGSAtmosphere";
    private const string SettingsType = "BGSAtmosphere::AtmosphereSettings";
    private const string OverridesType = "BGSAtmosphere::OverrideSettings";
    private const string MiscType = "BGSAtmosphere::MiscSettings";
    private const uint TypeRef = 0xFFFFFF05;
    private const uint TypeUInt32 = 0xFFFFFF0D;

    [Fact]
    public void ParseAll_DecodesFullAndDiffEnvelopesAndResolvesStructuralReferences()
    {
        const uint rootFormId = 0x0020CDD3;
        const uint earthFormId = 0x0000C9D1;
        const uint earthClimateFormId = 0x00064D14;
        var rootBytes = AtmosphereRecord(
            rootFormId,
            ("EDID", NullTermString("AtmosphereMedium")),
            ("REFL", BuildFullStream(0, 0, 0)));
        var earthBytes = AtmosphereRecord(
            earthFormId,
            ("EDID", NullTermString("EarthAtmosphere")),
            ("RFDP", U32(rootFormId)),
            ("RDIF", BuildDiffStream(rootFormId, earthClimateFormId)));

        var parsed = ParseRecords(rootBytes, earthBytes);

        Assert.Equal(2, parsed.Atmospheres.Count);
        Assert.DoesNotContain("ATMO", parsed.UnparsedTypeCounts.Keys);
        var root = parsed.Atmospheres.Single(record => record.FormId == rootFormId);
        Assert.Equal(StarfieldAtmospherePayloadKind.FullObject, root.PayloadKind);
        Assert.Null(root.ParentFormId);
        Assert.Equal(0u, root.Patch?.ParentFormId);
        Assert.Equal(0u, root.Patch?.SunPresetOverrideFormId);
        Assert.Equal(0u, root.Patch?.ClimateOverrideFormId);
        Assert.Null(root.DecodeFailure);

        var earth = parsed.Atmospheres.Single(record => record.FormId == earthFormId);
        Assert.Equal("EarthAtmosphere", earth.EditorId);
        Assert.Equal(StarfieldAtmospherePayloadKind.Diff, earth.PayloadKind);
        Assert.Equal(rootFormId, earth.ParentFormId);
        Assert.Equal(rootFormId, earth.Patch?.ParentFormId);
        Assert.Null(earth.Patch?.SunPresetOverrideFormId);
        Assert.Equal(earthClimateFormId, earth.Patch?.ClimateOverrideFormId);
        Assert.Null(earth.DecodeFailure);

        var index = parsed.Atmospheres.ToDictionary(record => record.FormId);
        var resolution = StarfieldAtmosphereResolver.Resolve(earthFormId, index);
        Assert.True(resolution.IsResolved, resolution.FailureDetail);
        Assert.Equal(new[] { rootFormId, earthFormId }, resolution.InheritanceChain);
        Assert.Equal(0u, resolution.EffectivePatch?.SunPresetOverrideFormId);
        Assert.Equal(earthClimateFormId, resolution.EffectivePatch?.ClimateOverrideFormId);
    }

    [Fact]
    public void MalformedLaterOverride_ReplacesEarlierValidRecordAndRetainsSourceIdentity()
    {
        const uint formId = 0x0000C9D1;
        var valid = ParseRecords(AtmosphereRecord(
            formId,
            ("EDID", NullTermString("EarthAtmosphereBase")),
            ("REFL", BuildFullStream(0, 0, 0))));
        var malformed = ParseRecords(AtmosphereRecord(
            formId,
            ("EDID", NullTermString("EarthAtmosphereBrokenOverride")),
            ("XTRA", [1, 2, 3, 4])));

        var merged = valid.MergeWith(malformed);

        var retained = Assert.Single(merged.Atmospheres);
        Assert.Same(Assert.Single(malformed.Atmospheres), retained);
        Assert.Equal("EarthAtmosphereBrokenOverride", retained.EditorId);
        Assert.Equal(StarfieldAtmospherePayloadKind.Unknown, retained.PayloadKind);
        Assert.Null(retained.Patch);
        Assert.Contains("unsupported", retained.DecodeFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExactOuterEnvelope_RejectsDuplicateUnknownAndOutOfOrderFields()
    {
        var duplicate = ParseRecords(AtmosphereRecord(
            0x100,
            ("EDID", NullTermString("Duplicate")),
            ("EDID", NullTermString("DuplicateAgain")),
            ("REFL", BuildFullStream(0, 0, 0))));
        Assert.Contains("duplicate EDID", Assert.Single(duplicate.Atmospheres).DecodeFailure,
            StringComparison.OrdinalIgnoreCase);

        var unknown = ParseRecords(AtmosphereRecord(
            0x101,
            ("EDID", NullTermString("Unknown")),
            ("FULL", NullTermString("not part of retail ATMO")),
            ("REFL", BuildFullStream(0, 0, 0))));
        Assert.Contains("unsupported", Assert.Single(unknown.Atmospheres).DecodeFailure,
            StringComparison.OrdinalIgnoreCase);

        var outOfOrder = ParseRecords(AtmosphereRecord(
            0x102,
            ("RFDP", U32(0x200)),
            ("EDID", NullTermString("OutOfOrder")),
            ("RDIF", BuildDiffStream(0x200, null))));
        Assert.Contains("EDID", Assert.Single(outOfOrder.Atmospheres).DecodeFailure,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidEdidAndTruncatedTail_FailClosedButPreserveACompleteLeadingIdentity()
    {
        var invalidEdid = ParseRecords(AtmosphereRecord(
            0x103,
            ("EDID", [.. Encoding.ASCII.GetBytes("Bad\0Tail\0")]),
            ("REFL", BuildFullStream(0, 0, 0))));
        var invalid = Assert.Single(invalidEdid.Atmospheres);
        Assert.Null(invalid.EditorId);
        Assert.Contains("EDID", invalid.DecodeFailure, StringComparison.OrdinalIgnoreCase);

        var truncatedBytes = AtmosphereRecord(
            0x104,
            ("EDID", NullTermString("RetainedPrefix")),
            ("REFL", BuildFullStream(0, 0, 0)));
        Array.Resize(ref truncatedBytes, truncatedBytes.Length - 1);
        var truncated = Assert.Single(ParseRecords(truncatedBytes).Atmospheres);
        Assert.Equal("RetainedPrefix", truncated.EditorId);
        Assert.Contains("boundary", truncated.DecodeFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Null(truncated.Patch);
    }

    [Fact]
    public void DiffEnvelope_RejectsZeroOrContradictoryParentButRetainsEstablishedKind()
    {
        var zeroParent = Assert.Single(ParseRecords(AtmosphereRecord(
            0x106,
            ("EDID", NullTermString("ZeroParent")),
            ("RFDP", U32(0)),
            ("RDIF", BuildDiffStream(0, null)))).Atmospheres);
        Assert.Equal(StarfieldAtmospherePayloadKind.Diff, zeroParent.PayloadKind);
        Assert.Equal(0u, zeroParent.ParentFormId);
        Assert.Null(zeroParent.Patch);
        Assert.Contains("non-zero", zeroParent.DecodeFailure, StringComparison.OrdinalIgnoreCase);

        var contradiction = Assert.Single(ParseRecords(AtmosphereRecord(
            0x107,
            ("EDID", NullTermString("ContradictoryParent")),
            ("RFDP", U32(0x200)),
            ("RDIF", BuildDiffStream(0x201, null)))).Atmospheres);
        Assert.Equal(StarfieldAtmospherePayloadKind.Diff, contradiction.PayloadKind);
        Assert.Equal(0x200u, contradiction.ParentFormId);
        Assert.Null(contradiction.Patch);
        Assert.Contains("disagrees", contradiction.DecodeFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BigEndianAndPartiallyRecoveredRecords_FailClosed()
    {
        const uint formId = 0x105;
        var bigEndianBytes = BuildRecordBytes(
            formId,
            "ATMO",
            true,
            ("EDID", NullTermString("BigEndianAtmosphere")),
            ("REFL", BuildFullStream(0, 0, 0)));
        var bigEndian = Assert.Single(ParseRecords(bigEndianBytes, isBigEndian: true).Atmospheres);
        Assert.Equal("BigEndianAtmosphere", bigEndian.EditorId);
        Assert.True(bigEndian.IsBigEndian);
        Assert.Contains("little-endian", bigEndian.DecodeFailure, StringComparison.OrdinalIgnoreCase);

        var littleEndianBytes = AtmosphereRecord(
            formId,
            ("EDID", NullTermString("RecoveredAtmosphere")),
            ("REFL", BuildFullStream(0, 0, 0)));
        var descriptor = Descriptor(littleEndianBytes, 0, formId, false);
        var context = new RecordParserContext(
            new EsmRecordScanResult
            {
                Game = BethesdaGame.Starfield,
                MainRecords = [descriptor]
            },
            null,
            new ByteArrayMemoryAccessor(littleEndianBytes),
            littleEndianBytes.Length,
            null);
        context.PartiallyRecoveredFormIds.Add(formId);

        var recovered = Assert.Single(new MiscEnvironmentHandler(context).ParseStarfieldAtmospheres());
        Assert.Equal("RecoveredAtmosphere", recovered.EditorId);
        Assert.Contains("partially recovered", recovered.DecodeFailure, StringComparison.OrdinalIgnoreCase);
    }

    private static RecordCollection ParseRecords(params byte[][] recordBytes) =>
        ParseRecords(recordBytes, false);

    private static RecordCollection ParseRecords(byte[][] recordBytes, bool isBigEndian)
    {
        var totalLength = recordBytes.Sum(bytes => bytes.Length);
        var allBytes = new byte[totalLength];
        var descriptors = new List<DetectedMainRecord>(recordBytes.Length);
        var offset = 0;
        foreach (var bytes in recordBytes)
        {
            Array.Copy(bytes, 0, allBytes, offset, bytes.Length);
            var formId = isBigEndian
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12))
                : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
            descriptors.Add(Descriptor(bytes, offset, formId, isBigEndian));
            offset += bytes.Length;
        }

        using var mmf = MemoryMappedFile.CreateNew(null, allBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, allBytes.Length);
        accessor.WriteArray(0, allBytes, 0, allBytes.Length);
        var scan = new EsmRecordScanResult
        {
            Game = BethesdaGame.Starfield,
            MainRecords = descriptors
        };
        return new RecordParser(scan, accessor: accessor, fileSize: allBytes.Length).ParseAll();
    }

    private static RecordCollection ParseRecords(byte[] recordBytes, bool isBigEndian) =>
        ParseRecords([recordBytes], isBigEndian);

    private static DetectedMainRecord Descriptor(
        byte[] bytes,
        long offset,
        uint formId,
        bool isBigEndian) =>
        new("ATMO", (uint)(bytes.Length - 24), 0, formId, offset, isBigEndian);

    private static byte[] AtmosphereRecord(
        uint formId,
        params (string Signature, byte[] Data)[] fields) =>
        BuildRecordBytes(formId, "ATMO", false, fields);

    private static byte[] BuildFullStream(uint parent, uint sun, uint climate)
    {
        var schema = BuildSchema();
        return ReflectionStream(
            schema.StringTable,
            [.. schema.ClassChunks, Chunk("OBJT", Concat(
                U32(schema.Offsets[RootType]), Ref(parent), Ref(sun), Ref(climate))) ]);
    }

    private static byte[] BuildDiffStream(uint parent, uint? climate)
    {
        var schema = BuildSchema();
        var body = new List<byte>();
        body.AddRange(U32(schema.Offsets[RootType]));
        body.AddRange(U16(0)); // Settings
        body.AddRange(U16(0)); // pParent
        body.AddRange(Ref(parent));
        if (climate.HasValue)
        {
            body.AddRange(U16(2)); // Misc
            body.AddRange(U16(0)); // pClimateOverride
            body.AddRange(Ref(climate.Value));
            body.AddRange(U16(ushort.MaxValue));
        }

        body.AddRange(U16(ushort.MaxValue));
        body.AddRange(U16(ushort.MaxValue));
        return ReflectionStream(schema.StringTable, [.. schema.ClassChunks, Chunk("DIFF", [.. body])]);
    }

    private static ReflectionSchema BuildSchema()
    {
        string[] names =
        [
            RootType, SettingsType, OverridesType, MiscType, "Settings", "pParent", "Overrides",
            "Misc", "pSunPresetOverride", "pClimateOverride"
        ];
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        var strings = new List<byte>();
        foreach (var name in names)
        {
            offsets[name] = checked((uint)strings.Count);
            strings.AddRange(Encoding.ASCII.GetBytes(name));
            strings.Add(0);
        }

        var chunks = new List<byte[]>
        {
            Chunk("TYPE", U32(4)),
            ClassChunk(offsets, OverridesType, ("pSunPresetOverride", TypeRef)),
            ClassChunk(offsets, MiscType, ("pClimateOverride", TypeRef)),
            ClassChunk(offsets, SettingsType,
                ("pParent", TypeRef),
                ("Overrides", offsets[OverridesType]),
                ("Misc", offsets[MiscType])),
            ClassChunk(offsets, RootType, ("Settings", offsets[SettingsType]))
        };
        return new ReflectionSchema(offsets, [.. strings], chunks);
    }

    private static byte[] ClassChunk(
        IReadOnlyDictionary<string, uint> offsets,
        string className,
        params (string Name, uint Type)[] fields)
    {
        var body = new List<byte>();
        body.AddRange(U32(offsets[className]));
        body.AddRange(U32(0));
        body.AddRange(U16(0));
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

    private static byte[] ReflectionStream(byte[] strings, IReadOnlyList<byte[]> chunks) =>
        Concat(
            Encoding.ASCII.GetBytes("BETH"), U32(8), U32(4), U32((uint)chunks.Count + 2),
            Encoding.ASCII.GetBytes("STRT"), U32((uint)strings.Length), strings,
            Concat([.. chunks]));

    private static byte[] Chunk(string signature, byte[] body) =>
        Concat(Encoding.ASCII.GetBytes(signature), U32((uint)body.Length), body);

    private static byte[] Ref(uint value) => Concat(U32(TypeUInt32), U32(value));

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);

    private static byte[] U16(ushort value) => BitConverter.GetBytes(value);

    private static byte[] Concat(params byte[][] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts)
        {
            bytes.AddRange(part);
        }

        return [.. bytes];
    }

    private sealed record ReflectionSchema(
        IReadOnlyDictionary<string, uint> Offsets,
        byte[] StringTable,
        IReadOnlyList<byte[]> ClassChunks);
}
